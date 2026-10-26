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

app.MapPost("/protegey/report-transaction", async (HttpContext http, ProtegeyClient protegey) =>
{
    return await protegey.Transactions.ReportAsync(new Dictionary<string, object?>
    {
        ["externalTransactionId"] = $"dotnet-example-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        ["externalCustomerId"] = CustomerId(http.Session),
        ["direction"] = "DEBIT",
        ["amount"] = 5000,
        ["currency"] = "XAF",
        ["transactionType"] = "test",
    });
});

app.MapPost("/protegey/start-kyc", async (HttpContext http, ProtegeyClient protegey) =>
{
    return await protegey.Kyc.StartSessionAsync(CustomerId(http.Session));
});

// Route a partner would point their Protegey webhook URL at.
app.MapPost("/protegey/webhook", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync(); // raw body — required, a re-serialized one won't match
    var timestamp = request.Headers["X-Timestamp"].ToString();
    var signature = request.Headers["X-Signature"].ToString();

    if (!WebhookVerifier.Verify(rawBody, timestamp, signature, webhookSecret))
    {
        return Results.Json(new { error = "invalid signature" }, statusCode: 401);
    }

    // Parse rawBody here and update the matching case/session in your own system.
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
