using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaFraudApi.Config;
using RinhaFraudApi.Models;
using RinhaFraudApi.Services;

ThreadPool.GetMinThreads(out var wt, out var io);
ThreadPool.SetMinThreads(Math.Max(wt, 32), Math.Max(io, 32));
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.NumberHandling =
        JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxConcurrentConnections = 2048;
    options.Limits.MinRequestBodyDataRate = null;
    options.Limits.MinResponseDataRate = null;
});

var app = builder.Build();
var referencesPath = Environment.GetEnvironmentVariable("REFERENCES_PATH") ?? Path.Combine(AppContext.BaseDirectory, "data", "references.bin");
var hammingRadius = int.TryParse(Environment.GetEnvironmentVariable("KNN_HAMMING_RADIUS"), out var radius)
    ? Math.Clamp(radius, 0, ReferenceStore.Dimensions)
    : 1;
var maxCandidates = int.TryParse(Environment.GetEnvironmentVariable("KNN_MAX_CANDIDATES"), out var candidates)
    ? Math.Max(ReferenceStore.K, candidates)
    : 2048;
var normalizationPath = Path.Combine(AppContext.BaseDirectory, "Resources", "normalization.json");
var mccPath = Path.Combine(AppContext.BaseDirectory, "Resources", "mcc_risk.json");

var jsonRead = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
};

var normalization = JsonSerializer.Deserialize<NormalizationConfig>(await File.ReadAllTextAsync(normalizationPath), jsonRead)
    ?? throw new InvalidOperationException("normalization.json inválido.");
var mccRisk = JsonSerializer.Deserialize<Dictionary<string, double>>(await File.ReadAllTextAsync(mccPath), jsonRead)
    ?? throw new InvalidOperationException("mcc_risk.json inválido.");

var jsonBodyOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    NumberHandling =
        JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

ReferenceStore? store = null;
if (ReferenceStore.TryOpen(referencesPath, hammingRadius, maxCandidates, out var opened))
{
    store = opened;
}

app.MapGet("/ready", async context =>
{
    context.Response.ContentType = "application/json";
    if (store is null || !store.IsValid)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsync("{\"status\":\"not_ready\",\"reason\":\"references_unavailable\"}");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    await context.Response.WriteAsync("{\"status\":\"ok\"}");
});

app.MapPost("/fraud-score", async (HttpRequest httpRequest, CancellationToken cancellationToken) =>
{
    if (store is null || !store.IsValid)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(httpRequest.Body, cancellationToken: cancellationToken);
    }
    catch (BadHttpRequestException)
    {
        return Results.BadRequest();
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }

    using (doc)
    {
        var root = doc.RootElement;
        var payload = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("request", out var reqEl)
            && reqEl.ValueKind == JsonValueKind.Object
            ? reqEl
            : root;
        FraudScoreRequest? req;
        try
        {
            req = payload.Deserialize<FraudScoreRequest>(jsonBodyOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (req is null)
        {
            return Results.BadRequest();
        }

        var vec = new float[ReferenceStore.Dimensions];
        Vectorizer.BuildVector(req, mccRisk, normalization, vec.AsSpan());

        var fraudCount = store.SearchKnnFraudCount(vec);
        var responseJson = fraudCount switch
        {
            0 => "{\"approved\":true,\"fraud_score\":0}",
            1 => "{\"approved\":true,\"fraud_score\":0.2}",
            2 => "{\"approved\":true,\"fraud_score\":0.4}",
            3 => "{\"approved\":false,\"fraud_score\":0.6}",
            4 => "{\"approved\":false,\"fraud_score\":0.8}",
            _ => "{\"approved\":false,\"fraud_score\":1}"
        };

        return Results.Text(responseJson, "application/json");
    }
});

app.Run();
