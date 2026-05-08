using System.Text.Json.Serialization;

namespace RinhaFraudApi.Models;

public sealed class FraudScoreRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("transaction")]
    public required TransactionDto Transaction { get; init; }

    [JsonPropertyName("customer")]
    public required CustomerDto Customer { get; init; }

    [JsonPropertyName("merchant")]
    public required MerchantDto Merchant { get; init; }

    [JsonPropertyName("terminal")]
    public required TerminalDto Terminal { get; init; }

    [JsonPropertyName("last_transaction")]
    public LastTransactionDto? LastTransaction { get; init; }
}

public sealed class TransactionDto
{
    [JsonPropertyName("amount")]
    public required double Amount { get; init; }

    [JsonPropertyName("installments")]
    public required int Installments { get; init; }

    [JsonPropertyName("requested_at")]
    public required DateTimeOffset RequestedAt { get; init; }
}

public sealed class CustomerDto
{
    [JsonPropertyName("avg_amount")]
    public required double AvgAmount { get; init; }

    [JsonPropertyName("tx_count_24h")]
    public required int TxCount24H { get; init; }

    /// <summary>
    /// API permite lista vazia; alguns clientes omitem o campo ou enviam null — tratar como [] no vetor.
    /// </summary>
    [JsonPropertyName("known_merchants")]
    public IReadOnlyList<string>? KnownMerchants { get; init; }
}

public sealed class MerchantDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("mcc")]
    public required string Mcc { get; init; }

    [JsonPropertyName("avg_amount")]
    public required double AvgAmount { get; init; }
}

public sealed class TerminalDto
{
    [JsonPropertyName("is_online")]
    public required bool IsOnline { get; init; }

    [JsonPropertyName("card_present")]
    public required bool CardPresent { get; init; }

    [JsonPropertyName("km_from_home")]
    public required double KmFromHome { get; init; }
}

public sealed class LastTransactionDto
{
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("km_from_current")]
    public required double KmFromCurrent { get; init; }
}

public sealed class FraudScoreResponse
{
    [JsonPropertyName("approved")]
    public required bool Approved { get; init; }

    [JsonPropertyName("fraud_score")]
    public required double FraudScore { get; init; }
}
