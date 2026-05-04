using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaFraudApi.Config;
using RinhaFraudApi.Models;
using RinhaFraudApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
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

ReferenceStore? store = null;
if (ReferenceStore.TryOpen(referencesPath, out var opened))
{
    store = opened;
}

app.MapGet("/ready", () => store is { IsValid: true }
    ? Results.Ok()
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPost("/fraud-score", (FraudScoreRequest req) =>
{
    if (store is null || !store.IsValid)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    Span<float> vec = stackalloc float[ReferenceStore.Dimensions];
    Vectorizer.BuildVector(req, mccRisk, normalization, vec);

    var fraudCount = store.SearchKnnFraudCount(vec);
    var fraudScore = fraudCount / 5.0;
    var approved = fraudScore < 0.6;

    return Results.Ok(new FraudScoreResponse
    {
        Approved = approved,
        FraudScore = fraudScore,
    });
});

app.Run();
