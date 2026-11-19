using Protegey.Sdk;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();

// Read as flat keys, not the nested "Protegey:ApiKey" shape — AddEnvironmentVariables() (wired
// in by WebApplication.CreateBuilder by default) maps a literal env var name straight onto a
// flat configuration key with no prefix needed, so PROTEGEY_API_KEY works exactly as run here
// and in every other language's example, no ASPNETCORE_-specific env var naming to remember.
var apiKey = builder.Configuration["PROTEGEY_API_KEY"]
    ?? throw new InvalidOperationException("PROTEGEY_API_KEY is required");
var baseUrl = builder.Configuration["PROTEGEY_BASE_URL"] ?? "https://api.protegey.com";
var webhookSecret = builder.Configuration["PROTEGEY_WEBHOOK_SECRET"]
    ?? throw new InvalidOperationException("PROTEGEY_WEBHOOK_SECRET is required");

builder.Services.AddSingleton(new ProtegeyClient(apiKey, baseUrl));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();

// Turn an API error into the same status for the demo page instead of a 500.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (ProtegeyApiException e)
    {
        context.Response.StatusCode = e.Status == 0 ? 502 : e.Status;
        await context.Response.WriteAsJsonAsync(new { error = e.Message, errorCode = e.ErrorCode });
    }
});

app.MapPost("/protegey/report-transaction", async (HttpContext http, ProtegeyClient protegey) =>
{
    // Returns {transactionId, decision (CLEAR|REVIEW|BLOCKED), riskScore, deviceAction, alerts[]}
    return await protegey.Transactions.ReportAsync(new Dictionary<string, object?>
    {
        ["externalTransactionId"] = $"dotnet-example-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        ["externalCustomerId"] = CustomerId(http.Session),
        ["direction"] = "DEBIT",
        ["amount"] = 5000,
        ["currency"] = "XAF",
        ["transactionType"] = "test",
        ["channel"] = "online",
    });
});

app.MapGet("/protegey/alerts", async (ProtegeyClient protegey) =>
{
    // The SDK lowercases the status filter — the alerts list only accepts lowercase.
    return await protegey.Alerts.ListAsync(new Dictionary<string, object?> { ["status"] = "OPEN", ["limit"] = 10 });
});

app.MapPost("/protegey/start-kyc", async (HttpContext http, ProtegeyClient protegey) =>
{
    // Returns {sessionId, url} — url is single-use and valid for 30 minutes.
    return await protegey.Kyc.StartSessionAsync(CustomerId(http.Session));
});

app.MapGet("/protegey/kyc-sessions", async (HttpContext http, ProtegeyClient protegey) =>
{
    // Exact match on our own customer id. Each row carries status (the final decision),
    // recommendation (Protegey's verdict: APPROVE | REVIEW | REJECT | null) and images — pre-signed
    // links valid 1 hour, re-issued on every call, so the page asks again instead of caching them.
    return await protegey.Kyc.ListSessionsAsync(new Dictionary<string, object?>
    {
        ["externalUserId"] = CustomerId(http.Session),
        ["limit"] = 10,
    });
});

app.MapPost("/protegey/cash-deposit", async (HttpContext http, ProtegeyClient protegey) =>
{
    // Our own reference: sending it makes retries safe (a second POST with it answers 409).
    var reference = $"dotnet-deposit-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    // Returns {cashDepositId, externalTransactionId, decision (CLEAR|REVIEW|BLOCKED), riskScore, alerts[]}
    var recorded = await protegey.CashDeposits.RecordAsync(new Dictionary<string, object?>
    {
        ["externalTransactionId"] = reference,
        ["externalCustomerId"] = CustomerId(http.Session),
        ["amount"] = 2500000,
        ["currency"] = "XAF",
        ["channel"] = "BRANCH",
        ["branchId"] = "DLA-AKWA",
        ["depositorName"] = "Demo Depositor",
        ["metadata"] = new Dictionary<string, object?> { ["tellerId"] = "T-04" },
    });
    // Look it up by our own reference — amount comes back as a decimal string ("2500000.00").
    var stored = await protegey.CashDeposits.GetAsync(reference);
    return new { recorded, stored };
});

app.MapGet("/protegey/cash-deposits", async (HttpContext http, ProtegeyClient protegey) =>
{
    return await protegey.CashDeposits.ListAsync(new Dictionary<string, object?>
    {
        ["externalCustomerId"] = CustomerId(http.Session),
        ["dateFrom"] = DateTime.UtcNow.AddDays(-30),
        ["limit"] = 20,
    });
});

app.MapPost("/protegey/screen", async (ProtegeyClient protegey) =>
{
    return await protegey.Sanctions.ScreenAsync("John Doe", "person");
});

app.MapPost("/protegey/invite-business", async (ProtegeyClient protegey) =>
{
    // contactEmail or contactPhone (at least one). With only a phone no email is sent —
    // deliver the returned applicationUrl yourself (SMS, WhatsApp, ...).
    return await protegey.Kyb.InviteApplicantAsync("Demo Business Contact", contactPhone: "+237690000000");
});

app.MapPost("/protegey/shared-signal", async (HttpContext http, ProtegeyClient protegey) =>
{
    // Requires the shared fraud signal network to be enabled for 7 days, otherwise 400.
    return await protegey.SharedSignal.CheckAsync(new Dictionary<string, object?>
    {
        ["phoneNumber"] = "+237690000000",
        ["externalCustomerId"] = CustomerId(http.Session),
    });
});

// Route a partner would point their Protegey webhook URL at (must be HTTPS in production).
app.MapPost("/protegey/webhook", async (HttpRequest request, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(); // raw body — required, a re-serialized one won't match
    var timestamp = request.Headers["X-Timestamp"].ToString();
    var signature = request.Headers["X-Signature"].ToString();

    if (!WebhookVerifier.Verify(rawBody, timestamp, signature, webhookSecret))
    {
        return Results.Json(new { error = "invalid signature" }, statusCode: 401);
    }

    using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
    var root = doc.RootElement;
    var type = root.GetProperty("type").GetString();
    // Deliveries are retried (up to 5 attempts over ~3 h) — deduplicate on eventId in a real integration.
    var eventId = root.GetProperty("eventId").GetString();

    switch (type)
    {
        case WebhookEventTypes.KycStatusChanged:
            // Sent on a FaceTec result and on manual status changes. sessionId is the same id
            // StartSessionAsync returned; recommendation is Protegey's verdict, status the final decision.
            logger.LogInformation("KYC {EventId}: session {Session} ({User}) -> {Status}, recommendation {Recommendation}",
                eventId, root.GetProperty("sessionId"), root.GetProperty("externalUserId"), root.GetProperty("status"), root.GetProperty("recommendation"));
            break;
        case WebhookEventTypes.KybStatusChanged:
            logger.LogInformation("KYB {EventId}: {Client} -> {Status} (submission {SubmissionStatus})",
                eventId, root.GetProperty("clientId"), root.GetProperty("status"), root.GetProperty("submissionStatus"));
            break;
        case WebhookEventTypes.AlertCreated:
        case WebhookEventTypes.AlertStatusChanged:
            logger.LogInformation("{Type} {EventId}: alert {Alert} is {Status}", type, eventId, root.GetProperty("alertId"), root.GetProperty("status"));
            break;
        case WebhookEventTypes.TransactionBlocked:
            logger.LogInformation("Blocked {EventId}: {Tx}", eventId, root.GetProperty("externalTransactionId"));
            break;
        default:
            logger.LogInformation("Webhook {Type} {EventId}", type, eventId);
            break;
    }

    return Results.Ok(new { received = true });
});

app.Run();

// A fresh id per HTTP session — real integrations pass the end user's own stable id instead.
static string CustomerId(ISession session)
{
    var existing = session.GetString("protegeyDemoCustomerId");
    if (existing != null) return existing;
    var id = "customer-" + Guid.NewGuid().ToString("N")[..12];
    session.SetString("protegeyDemoCustomerId", id);
    return id;
}
