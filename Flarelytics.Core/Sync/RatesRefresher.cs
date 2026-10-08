using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Sync;

/// <summary>Tiene aggiornata la tabella dei cambi.</summary>
public class RatesRefresher(FlarelyticsDbContext db, EcbExchangeRates ecb, ILogger<RatesRefresher> log)
{
    /// <summary>Al primo avvio si prende un po' più dell'anno di storico dei report.</summary>
    private const int InitialDays = 400;

    public async Task<int> RefreshAsync(DateTime nowUtc, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var latest = await db.Set<ExchangeRate>().MaxAsync(r => (DateOnly?)r.Date, ct);

        // La BCE pubblica nei giorni lavorativi: se c'è già ieri, o se oggi è
        // lunedì e c'è venerdì, non c'è niente di nuovo da chiedere.
        if (latest is { } l && l >= today.AddDays(-1 - (today.DayOfWeek == DayOfWeek.Monday ? 2 : 0))) return 0;

        var since = latest?.AddDays(1) ?? today.AddDays(-InitialDays);
        var rates = await ecb.FetchHistoryAsync(since, ct);

        db.AddRange(rates);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Cambi BCE: {Count} nuovi valori dal {Since}", rates.Count, since);
        return rates.Count;
    }
}
