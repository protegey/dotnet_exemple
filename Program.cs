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
    // Deliveries are retried (up to 5 attempts) — deduplicate on eventId in a real integration.
    var eventId = root.GetProperty("eventId").GetString();

    switch (type)
    {
        case WebhookEventTypes.KycStatusChanged:
            // sessionId is always null on this event — correlate on externalUserId.
            logger.LogInformation("KYC {EventId}: {User} -> {Status}", eventId, root.GetProperty("externalUserId"), root.GetProperty("status"));
            break;
        case WebhookEventTypes.KybStatusChanged:
            logger.LogInformation("KYB {EventId}: {Client} -> {Status}", eventId, root.GetProperty("clientId"), root.GetProperty("status"));
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
