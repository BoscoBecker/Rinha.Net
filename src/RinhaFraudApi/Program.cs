using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaFraudApi.Config;
using RinhaFraudApi.Models;
using RinhaFraudApi.Services;

ThreadPool.GetMinThreads(out var wt, out var io);
ThreadPool.SetMinThreads(Math.Max(wt, 64), Math.Max(io, 64));

var builder = WebApplication.CreateBuilder(args);

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
    options.Limits.MaxConcurrentConnections = 1000;
});
builder.Logging.ClearProviders();

var app = builder.Build();
// k-NN mmap: REFERENCES_PATH ou <base>/data/references.bin (arquivo). Sem arquivo válido, TryOpen falha → store null → /ready e /fraud-score em 503.
var referencesPath = Environment.GetEnvironmentVariable("REFERENCES_PATH") ?? Path.Combine(AppContext.BaseDirectory, "data", "references.bin");
var normalizationPath = Path.Combine(AppContext.BaseDirectory, "Resources", "normalization.json");
var mccPath = Path.Combine(AppContext.BaseDirectory, "Resources", "mcc_risk.json");

var jsonRead = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
};

var normalization = JsonSerializer.Deserialize<NormalizationConfig>(await File.ReadAllTextAsync(normalizationPath),jsonRead)
    ?? throw new InvalidOperationException("normalization.json inválido.");

var mccRisk = JsonSerializer.Deserialize<Dictionary<string, double>>(
    await File.ReadAllTextAsync(mccPath),jsonRead)
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
if (ReferenceStore.TryOpen(referencesPath, out var opened))
{
    store = opened;
}

app.MapGet("/ready", async context =>
{
    context.Response.ContentType = "application/json";    
    context.Response.StatusCode = StatusCodes.Status200OK;
    await context.Response.WriteAsync("{\"status\":\"ok\"}");
});

app.MapPost("/fraud-score", async (HttpRequest httpRequest, CancellationToken cancellationToken) =>
{
    if (store is null || !store.IsValid)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    using var doc = await JsonDocument.ParseAsync(httpRequest.Body, cancellationToken: cancellationToken);
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
    var fraudScore = fraudCount / 5.0;
    if (double.IsNaN(fraudScore) || double.IsInfinity(fraudScore))
    {
        fraudScore = 0;
    }

    var approved = fraudScore < 0.6;

    return Results.Ok(new FraudScoreResponse
    {
        Approved = approved,
        FraudScore = fraudScore,
    });
});

app.Run();
