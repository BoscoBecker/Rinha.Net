using RinhaFraudApi.Config;
using RinhaFraudApi.Models;

namespace RinhaFraudApi.Services;

public static class Vectorizer
{
    private const int Dimensions = 14;

    public static void BuildVector(
        FraudScoreRequest request,
        IReadOnlyDictionary<string, double> mccRisk,
        NormalizationConfig n,
        Span<float> output)
    {
        if (output.Length != Dimensions)
        {
            throw new ArgumentException("O vetor de saída deve ter 14 dimensões.");
        }

        var t = request.Transaction;
        var c = request.Customer;
        var m = request.Merchant;
        var term = request.Terminal;

        double customerAvg = c.AvgAmount <= 0 ? 1e-9 : c.AvgAmount;

        output[0] = ClampF((float)(t.Amount / n.MaxAmount));
        output[1] = ClampF((float)(t.Installments / (double)n.MaxInstallments));
        output[2] = ClampF((float)((t.Amount / customerAvg) / n.AmountVsAvgRatio));
        output[3] = (float)(t.RequestedAt.Hour / 23.0);
        output[4] = (float)(DayOfWeekToRinha(t.RequestedAt.DayOfWeek) / 6.0);

        if (request.LastTransaction is null)
        {
            output[5] = -1f;
            output[6] = -1f;
        }
        else
        {
            var minutes = (t.RequestedAt - request.LastTransaction.Timestamp).TotalMinutes;
            if (double.IsNaN(minutes) || double.IsInfinity(minutes))
            {
                minutes = 0;
            }

            output[5] = ClampF((float)(minutes / n.MaxMinutes));
            output[6] = ClampF((float)(request.LastTransaction.KmFromCurrent / n.MaxKm));
        }

        output[7] = ClampF((float)(term.KmFromHome / n.MaxKm));
        output[8] = ClampF((float)(c.TxCount24H / (double)n.MaxTxCount24H));
        output[9] = term.IsOnline ? 1f : 0f;
        output[10] = term.CardPresent ? 1f : 0f;
        var known = c.KnownMerchants ?? Array.Empty<string>();
        output[11] = IsUnknownMerchant(m.Id, known) ? 1f : 0f;
        output[12] = (float)(mccRisk.TryGetValue(m.Mcc, out var risk) ? risk : 0.5);
        output[13] = ClampF((float)(m.AvgAmount / n.MaxMerchantAvgAmount));
    }

    private static int DayOfWeekToRinha(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static bool IsUnknownMerchant(string merchantId, IReadOnlyList<string> known)
    {
        for (var i = 0; i < known.Count; i++)
        {
            if (known[i] == merchantId)
            {
                return false;
            }
        }

        return true;
    }

    private static float ClampF(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
}
