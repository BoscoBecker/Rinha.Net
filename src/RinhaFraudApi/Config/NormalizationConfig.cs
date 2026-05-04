using System.Text.Json.Serialization;

namespace RinhaFraudApi.Config;

public sealed class NormalizationConfig
{
    [JsonPropertyName("max_amount")]
    public double MaxAmount { get; init; }

    [JsonPropertyName("max_installments")]
    public int MaxInstallments { get; init; }

    [JsonPropertyName("amount_vs_avg_ratio")]
    public double AmountVsAvgRatio { get; init; }

    [JsonPropertyName("max_minutes")]
    public double MaxMinutes { get; init; }

    [JsonPropertyName("max_km")]
    public double MaxKm { get; init; }

    [JsonPropertyName("max_tx_count_24h")]
    public int MaxTxCount24H { get; init; }

    [JsonPropertyName("max_merchant_avg_amount")]
    public double MaxMerchantAvgAmount { get; init; }
}
