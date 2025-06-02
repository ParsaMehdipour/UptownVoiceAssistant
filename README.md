# UptownVoiceAssistant

**UptownVoiceAssistant** is a voice-enabled IVR system built using [Twilio Voice](https://www.twilio.com/voice) and ASP.NET Minimal APIs. It is designed for **Uptown Eye Specialists** to automate patient interactions by collecting health card numbers, integrating with the **OscarAppointment** service, and recording patient messages for follow-up.

## 🚀 Features

* 📞 Automatically answers incoming calls to the clinic
* 🗣️ Guides patients using AI-enhanced voice prompts
* 🔗 Captures and validates health card numbers via speech
* 🔗 Connects to OscarAppointment API for patient lookup
* 🎤 Records detailed patient messages for follow-up
* 🔁 Handles input errors with built-in Twilio speech correction

## 🧰 Tech Stack

* **C#** with **.NET 8 Minimal API**
* **Twilio Voice + TwiML**
* **ASP.NET Core**
* REST integration with external **OscarAppointment API**

## 📷 Call Flow

1. **Inbound Call Answered**
2. Greeting: *"Welcome to Uptown Eye Specialists..."*
3. Prompt for health card number (with retry support)
4. POST health card number to your OscarAppointment API
5. If matched:

   * Confirm match and record patient message
6. Save or forward `.WAV` audio file as needed

## 📦 Folder Structure

```bash
UptownVoiceAssistant/
│
├── Program.cs           # Entry point with all logic via minimal API
├── Services/            # (Optional) API client for OscarAppointment
├── Helpers/             # Twilio utilities or validators
└── README.md
```

## 🔧 Requirements

* [.NET 8 SDK](https://dotnet.microsoft.com/download)
* [Twilio Account](https://twilio.com/)
* Twilio phone number with voice enabled
* Public HTTPS endpoint (use [ngrok](https://ngrok.com/) during local dev)

## 🚰 Setup & Run

```bash
git clone https://github.com/yourusername/UptownVoiceAssistant.git
cd UptownVoiceAssistant
dotnet restore
dotnet run
```

If using locally, expose your app with ngrok:

```bash
ngrok http https://localhost:5001
```

Then set your Twilio Voice webhook (on your Twilio number) to:

```
https://your-ngrok-url.ngrok.io/voice/welcome
```

## 🔐 Security Tip

If exposing your app to Twilio, use request validation with your `TWILIO_AUTH_TOKEN` to ensure calls are from Twilio.

## 📞 Twilio Configuration

* Set webhook URL: `/voice/welcome`
* Enable Speech Recognition in `<Gather>`
* Adjust language and timeout settings to suit your callers

## 📤 Integration

The service will `POST` the provided health card number to your existing OscarAppointment API, and branch logic based on the response.

## 📝 License

MIT – free to use, modify, and share.

---

> Built with ❤️ to help Uptown Eye Specialists streamline patient care.
