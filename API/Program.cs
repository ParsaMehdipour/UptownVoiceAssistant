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
    // Build an action URL that Twilio can reach (uses x-forwarded-* if available)
    var actionAbsolute = BuildPublicUrl(request, "/voice/process");

    var response = new VoiceResponse();

    var gather = new Gather(
        input: new[] { Gather.InputEnum.Dtmf },
        numDigits: 10,
        action: new Uri(actionAbsolute),
        method: "POST",
        timeout: 10,
        finishOnKey: "#"
    );

    gather.Say("Welcome to Uptown Eye Specialists. Please enter your ten digit health card number using your keypad followed by the pound key.", voice: "Polly.Joanna");
    response.Append(gather);

    // If gather returns with no digits
    response.Say("We did not receive any input.", voice: "Polly.Joanna");
    // Redirect back to the public URL for welcome (so Twilio will call the same public host)
    var redirectUrl = BuildPublicUrl(request, "/voice/welcome");
    response.Redirect(new Uri(redirectUrl), method: "POST");

    logger.LogInformation("Returning TwiML for /voice/welcome with action={Action}", actionAbsolute);
    return Results.Content(response.ToString(), "application/xml");
});

// ----------------- Process (log everything and handle errors) -----------------
app.MapPost("/voice/process", async (HttpRequest request, ILogger<Program> logger) =>
{
    try
    {
        // Read raw body again (middleware already buffered) and parsed form
        request.EnableBuffering();
        string raw = "";
        if (request.ContentLength > 0)
        {
            using var r = new StreamReader(request.Body, leaveOpen: true);
            raw = await r.ReadToEndAsync();
            request.Body.Position = 0;
        }

        var form = await request.ReadFormAsync();

        // Log parsed form fields (very explicit)
        logger.LogInformation("---- /voice/process invoked ----");
        foreach (var kv in form)
            logger.LogInformation("Form field: {Key} = {Value}", kv.Key, kv.Value.ToString());
        logger.LogInformation("Raw body (again): {Raw}", string.IsNullOrEmpty(raw) ? "<empty>" : raw);

        var digits = form["Digits"].ToString();
        var from = form["From"].ToString();

        logger.LogInformation("Parsed Digits='{Digits}', From='{From}'", digits, from);

        // Defensive sanitize
        var clean = new string((digits ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 10)
        {
            logger.LogWarning("Invalid digits length: {Len} (raw: {RawDigits})", clean.Length, digits);
            var respBad = new VoiceResponse();
            respBad.Say("The number you entered does not appear to be ten digits. Please try again.", voice: "Polly.Joanna");
            var redirectUrl = BuildPublicUrl(request, "/voice/welcome");
            respBad.Redirect(new Uri(redirectUrl), method: "POST");
            return Results.Content(respBad.ToString(), "application/xml");
        }

        // TODO: validate against DB/service
        var isValid = await ValidateHCNAsync(clean);
        if (!isValid)
        {
            logger.LogInformation("HCN not found: {HCN}", clean);
            var respNotFound = new VoiceResponse();
            respNotFound.Say("We couldn't find records for that number. Please try again.", voice: "Polly.Joanna");
            respNotFound.Redirect(new Uri(BuildPublicUrl(request, "/voice/welcome")), method: "POST");
            return Results.Content(respNotFound.ToString(), "application/xml");
        }

        logger.LogInformation("HCN validated: {HCN}", clean);

        var response = new VoiceResponse();
        response.Say("Great. We've found your records. Please leave a detailed message including your name and number.", voice: "Polly.Joanna");
        response.Record(maxLength: 120, action: new Uri(BuildPublicUrl(request, "/voice/recording-complete")), method: "POST");

        return Results.Content(response.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        // Log full exception details
        logger.LogError(ex, "Unhandled exception in /voice/process: {Message}", ex.Message);

        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Polly.Joanna");
        err.Hangup();

        // Return TwiML error and a 200 body (Twilio expects valid TwiML)
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
        response.Say("Thank you for your message. Goodbye!", voice: "Polly.Joanna");
        response.Hangup();
        return Results.Content(response.ToString(), "application/xml");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /voice/recording-complete");
        var err = new VoiceResponse();
        err.Say("We are sorry — an application error has occurred. Please try again later.", voice: "Polly.Joanna");
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
