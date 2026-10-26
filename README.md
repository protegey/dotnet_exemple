# Protegey — .NET example

Minimal ASP.NET Core app demonstrating `Protegey.Sdk` (.NET) from a real backend: transaction
reporting, starting a KYC session, and verifying an incoming webhook's HMAC signature.

## Run it

```bash
# Clone protegey_dotnet_sdk as a sibling directory first (not yet on NuGet.org):
git clone https://github.com/protegey/protegey_dotnet_sdk.git ../protegey_dotnet_sdk

PROTEGEY_API_KEY=your-api-key \
PROTEGEY_BASE_URL=https://api.protegey.com \
PROTEGEY_WEBHOOK_SECRET=your-webhook-secret \
dotnet run
```

Open `http://localhost:5000` (or whatever port `dotnet run` prints) — two buttons call the
backend directly.

## What it does

- `Program.cs` — wires a `Protegey` singleton from `PROTEGEY_*` environment variables, and:
  - `POST /protegey/report-transaction` — `protegey.Transactions.ReportAsync()`.
  - `POST /protegey/start-kyc` — `protegey.Kyc.StartSessionAsync()`, returns the hosted URL, which
    the static page opens in a new tab.
  - `POST /protegey/webhook` — verifies `X-Signature`/`X-Timestamp` via `WebhookVerifier.Verify()`
    before trusting the payload. Point your Protegey webhook URL at this route to see it in action.
