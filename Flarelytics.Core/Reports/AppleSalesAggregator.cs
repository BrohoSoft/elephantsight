using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Reports;

/// <summary>
/// Da righe del report di Apple a metriche per app e paese.
/// </summary>
/// <remarks>
/// Le regole vengono dalla tabella dei Product Type Identifier di Apple:
/// <list type="bullet">
/// <item><c>1, 1F, 1T, 1E, 1EP, 1EU, F1, 1-B, F1-B</c>: prima installazione
/// (gratuita o a pagamento);</item>
/// <item><c>3, 3F, 3T, F3</c>: riscaricamento;</item>
/// <item><c>7, 7F, 7T, F7</c>: aggiornamento;</item>
/// <item><c>IA*</c> e <c>FI1</c>: acquisto in-app, tranne <c>IA3</c> che è un
/// ripristino e non un acquisto.</item>
/// </list>
/// Unità negative sono rimborsi. Nei rimborsi Apple scrive negativo il prezzo
/// al cliente ma positivo il ricavo per unità: per non dipendere da questa
/// asimmetria gli importi si calcolano come unità × valore assoluto del prezzo,
/// e il segno lo danno le unità.
/// </remarks>
public static class AppleSalesAggregator
{
    /// <summary>Quando cambia il modo di contare, si alza: i report già salvati vengono rielaborati.</summary>
    public const int Version = 1;

    private static readonly HashSet<string> Downloads = ["1", "1F", "1T", "1E", "1EP", "1EU", "F1", "1-B", "F1-B"];
    private static readonly HashSet<string> Redownloads = ["3", "3F", "3T", "F3"];
    private static readonly HashSet<string> Updates = ["7", "7F", "7T", "F7"];

    public static bool IsAppRow(string productType) =>
        Downloads.Contains(productType) || Redownloads.Contains(productType) || Updates.Contains(productType);

    private static bool IsPurchase(string productType) =>
        (productType.StartsWith("IA", StringComparison.Ordinal) || productType == "FI1") && productType != "IA3";

    /// <param name="skuToAppleId">Per attribuire gli acquisti in-app alla loro app.</param>
    /// <param name="toEurMicros">Conversione in micro-euro; null se la valuta non ha un cambio.</param>
    /// <returns>Le metriche, e quante righe non si sono potute attribuire a un'app.</returns>
    public static (List<DailyAppMetric> Metrics, int UnattributedRows) Aggregate(
        Guid tenantId, DateOnly date, IEnumerable<AppleSalesRow> rows,
        IReadOnlyDictionary<string, string> skuToAppleId, Func<decimal, string, long?> toEurMicros)
    {
        var byKey = new Dictionary<(string App, string Country), DailyAppMetric>();
        var unattributed = 0;

        foreach (var row in rows)
        {
            var appId = IsAppRow(row.ProductType)
                ? row.AppleIdentifier
                : skuToAppleId.GetValueOrDefault(row.ParentIdentifier);

            if (string.IsNullOrEmpty(appId))
            {
                unattributed++;
                continue;
            }

            if (!byKey.TryGetValue((appId, row.CountryCode), out var m))
            {
                m = new DailyAppMetric { TenantId = tenantId, Store = Store.AppStore, AppId = appId, Date = date, CountryCode = row.CountryCode };
                byKey[(appId, row.CountryCode)] = m;
            }

            if (row.Units < 0)
            {
                m.Refunds += -row.Units;
            }
            else if (Downloads.Contains(row.ProductType)) m.Downloads += row.Units;
            else if (Redownloads.Contains(row.ProductType)) m.Redownloads += row.Units;
            else if (Updates.Contains(row.ProductType)) m.Updates += row.Units;
            else if (IsPurchase(row.ProductType)) m.InAppPurchases += row.Units;

            AddAmount(m, row.Units * Math.Abs(row.DeveloperProceeds), row.ProceedsCurrency, toEurMicros, proceeds: true);
            AddAmount(m, row.Units * Math.Abs(row.CustomerPrice), row.CustomerCurrency, toEurMicros, proceeds: false);
        }

        return (byKey.Values.ToList(), unattributed);
    }

    private static void AddAmount(DailyAppMetric m, decimal amount, string currency, Func<decimal, string, long?> toEurMicros, bool proceeds)
    {
        if (amount == 0) return;

        var micros = toEurMicros(amount, currency);
        if (micros is null)
        {
            m.HasUnconvertedAmounts = true;
            return;
        }

        if (proceeds) m.ProceedsEurMicros += micros.Value;
        else m.SalesEurMicros += micros.Value;
    }
}
