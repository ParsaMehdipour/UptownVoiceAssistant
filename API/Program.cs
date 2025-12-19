using Twilio.AspNet.Core;
using Twilio.TwiML;
using Twilio.TwiML.Voice;
using Task = System.Threading.Tasks.Task;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddTwilioClient();
builder.Logging.AddConsole();

var app = builder.Build();

// Global request logger middleware (reads raw body)
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();

    string body = "";
    if (context.Request.ContentLength > 0)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
    }

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("----- Incoming Request -----");
    logger.LogInformation("{Method} {Path}{Query}", context.Request.Method, context.Request.Path, context.Request.QueryString);
    foreach (var h in context.Request.Headers)
        logger.LogInformation("Header: {Key} = {Value}", h.Key, h.Value.ToString());
    logger.LogInformation("Raw Body: {Body}", string.IsNullOrEmpty(body) ? "<empty>" : body);
    logger.LogInformation("----------------------------");

    await next();
});

// Helper: compute a public absolute URL that respects proxies (x-forwarded-host / proto)
string BuildPublicUrl(HttpRequest req, string relativePath)
{
    var forwardedHost = req.Headers["x-forwarded-host"].FirstOrDefault();
    var forwardedProto = req.Headers["x-forwarded-proto"].FirstOrDefault();
    var host = !string.IsNullOrEmpty(forwardedHost) ? forwardedHost : req.Host.Value;
    var scheme = !string.IsNullOrEmpty(forwardedProto) ? forwardedProto : req.Scheme;
    // Ensure we return absolute Uri
    return $"{scheme}://{host.TrimEnd('/')}{relativePath}";
}

// ----------------- Welcome (DTMF gather) -----------------
app.MapPost("/voice/welcome", (HttpRequest request, ILogger<Program> logger) =>
{
    var actionAbsolute = BuildPublicUrl(request, "/voice/process-hcn");

    var response = new VoiceResponse();

    var gather = new Gather(
        input: new[] { Gather.InputEnum.Dtmf },
        numDigits: 10,
        action: new Uri(actionAbsolute),
        method: "POST",
        timeout: 10,
        finishOnKey: "#"
    );

    gather.Say("Welcome to Uptown Eye Specialists. Please enter your ten digit health card number using your keypad followed by the pound key.", voice: "Google.en-US-Chirp3-HD-Leda");
    response.Append(gather);

    // If gather returns with no digits
    response.Say("We did not receive any input.", voice: "Google.en-US-Chirp3-HD-Leda");
    response.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");

    logger.LogInformation("Returning TwiML for /voice/welcome with action={Action}", actionAbsolute);
    return Results.Content(response.ToString(), "application/xml");
});

// ----------------- Process HCN (validate and ask for DOB) -----------------
app.MapPost("/voice/process-hcn", async (HttpRequest request, ILogger<Program> logger) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        foreach (var kv in form) logger.LogInformation("Form field: {Key} = {Value}", kv.Key, kv.Value.ToString());

        var digits = form["Digits"].ToString();
        var from = form["From"].ToString();
        logger.LogInformation("Parsed Digits='{Digits}', From='{From}'", digits, from);

        var clean = new string((digits ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 10)
        {
            logger.LogWarning("Invalid digits length: {Len} (raw: {RawDigits})", clean.Length, digits);
            var respBad = new VoiceResponse();
            respBad.Say("The number you entered does not appear to be ten digits. Please try again.", voice: "Google.en-US-Chirp3-HD-Leda");
            respBad.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");
            return Results.Content(respBad.ToString(), "application/xml");
        }

        var isValid = await ValidateHCNAsync(clean);
        if (!isValid)
        {
            logger.LogInformation("HCN not found: {HCN}", clean);
            var respNotFound = new VoiceResponse();
            respNotFound.Say("We couldn't find records for that number. Please try again.", voice: "Google.en-US-Chirp3-HD-Leda");
            respNotFound.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");
            return Results.Content(respNotFound.ToString(), "application/xml");
        }

        logger.LogInformation("HCN validated: {HCN}", clean);

        // Save HCN in session-like way? Twilio doesn't maintain session: we pass HCN forward via query string on action URLs.
        var actionDob = BuildPublicUrl(request, $"/voice/process-dob?hcn={clean}");

        var resp = new VoiceResponse();
        // Gather DOB as 8 digits YYYYMMDD
        var gatherDob = new Gather(
            input: new[] { Gather.InputEnum.Dtmf },
            numDigits: 8,
            action: new Uri(actionDob),
            method: "POST",
            timeout: 10,
            finishOnKey: "#"
        );

        gatherDob.Say("Great. To confirm your record, please enter your birth date using eight digits. For example, enter year, month, day. For June first 1985, enter one nine eight five zero six zero one. Then press the pound key.", voice: "Google.en-US-Chirp3-HD-Leda");
        resp.Append(gatherDob);

        resp.Say("We did not receive your birth date.", voice: "Google.en-US-Chirp3-HD-Leda");
        resp.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");

        return Results.Content(resp.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /voice/process-hcn");
        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Google.en-US-Chirp3-HD-Leda");
        err.Hangup();
        return Results.Content(err.ToString(), "application/xml");
    }
});

// ----------------- Process DOB (validate and ask for name via speech) -----------------
app.MapPost("/voice/process-dob", async (HttpRequest request, ILogger<Program> logger) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        foreach (var kv in form) logger.LogInformation("Form field: {Key} = {Value}", kv.Key, kv.Value.ToString());

        var digits = form["Digits"].ToString();
        var from = form["From"].ToString();
        logger.LogInformation("Received DOB Digits='{Digits}', From='{From}'", digits, from);

        // get HCN from query string (we passed it)
        var hcn = request.Query["hcn"].ToString();
        logger.LogInformation("HCN from query string: {HCN}", hcn);

        var clean = new string((digits ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 8)
        {
            logger.LogWarning("Invalid DOB digits length: {Len} (raw: {RawDigits})", clean.Length, digits);
            var respBad = new VoiceResponse();
            respBad.Say("The date you entered does not appear to be eight digits. Please try again.", voice: "Google.en-US-Chirp3-HD-Leda");
            respBad.Redirect(new Uri(BuildPublicUrl(request, $"/voice/process-hcn")), method: "POST");
            return Results.Content(respBad.ToString(), "application/xml");
        }

        // parse YYYYMMDD
        if (!DateTime.TryParseExact(clean, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dob))
        {
            logger.LogWarning("DOB parse failed for input: {Input}", clean);
            var respBad = new VoiceResponse();
            respBad.Say("We could not interpret that date. Please try again.", voice: "Google.en-US-Chirp3-HD-Leda");
            respBad.Redirect(new Uri(BuildPublicUrl(request, $"/voice/process-hcn")), method: "POST");
            return Results.Content(respBad.ToString(), "application/xml");
        }

        // Convert to ISO 8601 string($date-time)
        var dobIso = dob.ToString("o"); // ISO 8601 with offset
        logger.LogInformation("Parsed DOB: {DOB} (ISO: {DOBIso})", dob, dobIso);

        // Ask for first and last name via speech
        var actionName = BuildPublicUrl(request, $"/voice/process-name?hcn={hcn}&dob={Uri.EscapeDataString(dobIso)}");

        var resp = new VoiceResponse();
        var gatherName = new Gather(
            input: new[] { Gather.InputEnum.Speech },
            action: new Uri(actionName),
            method: "POST",
            timeout: 5,
            speechTimeout: "auto",
            hints: "first name, last name" // optional
        );

        gatherName.Say("Thank you. Please clearly say your first name and last name after the tone. For example, John Smith.", voice: "Polly.Joanna");
        resp.Append(gatherName);

        resp.Say("We did not receive your name.", voice: "Google.en-US-Chirp3-HD-Leda");
        resp.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");

        return Results.Content(resp.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /voice/process-dob");
        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Google.en-US-Chirp3-HD-Leda");
        err.Hangup();
        return Results.Content(err.ToString(), "application/xml");
    }
});

// ----------------- Process Name (finalize & log everything) -----------------
app.MapPost("/voice/process-name", async (HttpRequest request, ILogger<Program> logger) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        foreach (var kv in form) logger.LogInformation("Form field: {Key} = {Value}", kv.Key, kv.Value.ToString());

        var speech = form["SpeechResult"].ToString();
        var from = form["From"].ToString();
        var hcn = request.Query["hcn"].ToString();
        var dobIso = request.Query["dob"].ToString(); // ISO string passed via query

        logger.LogInformation("Final collected values: HCN={HCN}, DOB={DOBIso}, SpeechResult={Speech}, From={From}",
            hcn, dobIso, speech, from);

        // Attempt to split speech into first/last (best-effort)
        string firstName = null, lastName = null;
        if (!string.IsNullOrWhiteSpace(speech))
        {
            var parts = speech.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                firstName = parts[0];
                lastName = string.Join(' ', parts.Skip(1));
            }
            else if (parts.Length == 1)
            {
                firstName = parts[0];
            }
        }

        logger.LogInformation("Parsed name: FirstName={First}, LastName={Last}", firstName ?? "<unknown>", lastName ?? "<unknown>");

        // At this point you have:
        //   hcn (10-digit), dobIso (ISO 8601 string), firstName, lastName, and the raw speech.
        // Log them (already logged above) and proceed to respond.

        var resp = new VoiceResponse();
        resp.Say($"Thank you {firstName ?? "caller"}. We have recorded your details. Goodbye.", voice: "Google.en-US-Chirp3-HD-Leda");
        resp.Hangup();

        return Results.Content(resp.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /voice/process-name");
        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Google.en-US-Chirp3-HD-Leda");
        err.Hangup();
        return Results.Content(err.ToString(), "application/xml");
    }
});
// recording-complete = same robust logging
app.MapPost("/voice/recording-complete", async (HttpRequest request, ILogger<Program> logger) =>
{
    try
    {
        var form = await request.ReadFormAsync();
        logger.LogInformation("---- /voice/recording-complete invoked ----");
        foreach (var kv in form) logger.LogInformation("{Key} = {Value}", kv.Key, kv.Value.ToString());
        var recordingUrl = form["RecordingUrl"].ToString();
        var caller = form["From"].ToString();
        logger.LogInformation("Caller: {Caller}, RecordingUrl: {RecordingUrl}", caller, recordingUrl);

        var response = new VoiceResponse();
        response.Say("Thank you for your message. Goodbye!", voice: "Google.en-US-Chirp3-HD-Leda");
        response.Hangup();
        return Results.Content(response.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /voice/recording-complete");
        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Google.en-US-Chirp3-HD-Leda");
        err.Hangup();
        return Results.Content(err.ToString(), "application/xml");
    }
});

app.Run();

// Simple validator (same as before)
Task<bool> ValidateHCNAsync(string hcn)
{
    Console.WriteLine($"Validating HCN: {hcn}");
    if (string.IsNullOrWhiteSpace(hcn)) return Task.FromResult(false);
    if (hcn.Length == 10 && long.TryParse(hcn, out _)) return Task.FromResult(true);
    return Task.FromResult(false);
}
