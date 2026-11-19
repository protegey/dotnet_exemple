# Protegey — .NET example

Minimal ASP.NET Core app demonstrating `Protegey.Sdk` (.NET) from a real backend: transaction
reporting, cash deposits, alerts, a KYC session and its photos, sanctions screening, a KYB invitation, a shared fraud signal
check, and verifying an incoming webhook's HMAC signature.

## Run it

```bash
# Clone protegey_dotnet_sdk as a sibling directory first (not yet on NuGet.org):
git clone https://github.com/protegey/protegey_dotnet_sdk.git ../protegey_dotnet_sdk

PROTEGEY_API_KEY=your-api-key \
PROTEGEY_BASE_URL=https://api.protegey.com \
PROTEGEY_WEBHOOK_SECRET=your-webhook-secret \
dotnet run
```

Use a **sandbox** API key while trying it out — sandbox vs live is decided only by which key you
use. Open `http://localhost:5000` (or whatever port `dotnet run` prints); each button calls the
backend directly.

## What it does

- `Program.cs` — wires a `ProtegeyClient` singleton from `PROTEGEY_*` environment variables, and:
  - `POST /protegey/report-transaction` — `protegey.Transactions.ReportAsync()`.
  - `GET /protegey/alerts` — `protegey.Alerts.ListAsync()` filtered on open alerts.
  - `POST /protegey/start-kyc` — `protegey.Kyc.StartSessionAsync()`, returns the hosted URL, which
    the static page opens in a new tab.
  - `GET /protegey/kyc-sessions` — `protegey.Kyc.ListSessionsAsync()` filtered on this demo
    customer's `externalUserId` (exact match). The page shows each session's `recommendation`
    and its photos from the `images` block (pre-signed links, valid 1 hour, re-issued per call).
  - `POST /protegey/cash-deposit` — `protegey.CashDeposits.RecordAsync()` with our own
    `externalTransactionId` (a reused one answers 409), then `GetAsync()` by that same reference.
  - `GET /protegey/cash-deposits` — `protegey.CashDeposits.ListAsync()` for this customer over the
    last 30 days.
  - `POST /protegey/screen` — `protegey.Sanctions.ScreenAsync()`.
  - `POST /protegey/invite-business` — `protegey.Kyb.InviteApplicantAsync()` with a phone number
    only; the response's `applicationUrl` is the link to send the business.
  - `POST /protegey/shared-signal` — `protegey.SharedSignal.CheckAsync()` (needs the network
    enabled for 7 days on your account, otherwise the API answers 400).
  - `POST /protegey/webhook` — verifies `X-Signature`/`X-Timestamp` via `WebhookVerifier.Verify()`
    before trusting the payload, then dispatches on the payload's `type`
    (`WebhookEventTypes`). Point your Protegey webhook URL at this route to see it in action.
  - API errors (`ProtegeyApiException`) are returned to the page with their status and
    `errorCode`.
