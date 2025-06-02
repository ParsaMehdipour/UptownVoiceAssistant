using Twilio.AspNet.Core;
using Twilio.TwiML;
using Twilio.TwiML.Voice;
using Task = System.Threading.Tasks.Task;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddTwilioClient(); // Optional if you need Twilio REST API access

var app = builder.Build();

// 1. Initial call - Welcome message + Ask for HCN
app.MapPost("/voice/welcome", () =>
{
    var response = new VoiceResponse();
    response.Say("Welcome to Uptown Eye Specialists.", voice: "Polly.Joanna");
    response.Say("To start, please say your 10-digit health card number after the beep.", voice: "Polly.Joanna");

    response.Append(new Gather(
        input: new[] { Gather.InputEnum.Speech, },
        action: new Uri("/voice/process", UriKind.Relative),
        method: "POST",
        timeout: 5,
        speechTimeout: "auto",
        language: "en-US"
    ));

    return Results.Content(response.ToString(), "application/xml");
});

// 2. Process speech result from HCN
app.MapPost("/voice/process", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var speechResult = form["SpeechResult"].ToString();

    // TODO: Validate HCN with your service
    var isValid = await ValidateHCNAsync(speechResult); // Implement this function

    var response = new VoiceResponse();

    if (!isValid)
    {
        response.Say("Sorry, we could not find your records. Let's try again.", voice: "Polly.Joanna");
        response.Redirect(new Uri("/voice/welcome"), method: "POST");
    }
    else
    {
        response.Say("Great. We've found your records!", voice: "Polly.Joanna");
        response.Say("Please leave a detailed message including your name and number. One of our healthcare consultants will contact you within two business days.", voice: "Polly.Joanna");

        response.Record(
            maxLength: 120,
            action: new Uri("/voice/recording-complete", UriKind.Relative),
            method: "POST"
        );
    }

    return Results.Content(response.ToString(), "application/xml");
});

// 3. Handle recorded message
app.MapPost("/voice/recording-complete", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var recordingUrl = form["RecordingUrl"];
    var caller = form["From"];

    // TODO: Save recording URL, or email it, or push to database
    Console.WriteLine($"Caller: {caller}, Recording: {recordingUrl}");

    var response = new VoiceResponse();
    response.Say("Thank you for your message. Goodbye!", voice: "Polly.Joanna");
    response.Hangup();

    return Results.Content(response.ToString(), "application/xml");
});

app.Run();

// Replace this with your actual HCN validation logic
Task<bool> ValidateHCNAsync(string hcn)
{
    Console.WriteLine($"Validating HCN: {hcn}");

    // Example: fake validation logic
    if (hcn.Length == 10 && long.TryParse(hcn, out _))
        return Task.FromResult(true);

    return Task.FromResult(false);
}
