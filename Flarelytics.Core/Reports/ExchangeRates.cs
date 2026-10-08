using System.Globalization;
using System.IO.Compression;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Core.Reports;

/// <summary>
/// I cambi di riferimento della BCE.
/// </summary>
/// <remarks>
/// Si scarica lo storico completo (un CSV in uno zip di qualche centinaio di
/// KB) e si inseriscono solo i giorni che mancano: un formato solo, e nessuna
/// differenza fra il primo avvio e i successivi. La BCE pubblica verso le 16
/// dei giorni lavorativi.
/// </remarks>
public class EcbExchangeRates(HttpClient http)
{
    public const string HistoryUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.zip";

    public async Task<List<ExchangeRate>> FetchHistoryAsync(DateOnly since, CancellationToken ct)
    {
        var zip = await http.GetByteArrayAsync(HistoryUrl, ct);
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.Entries.Single().Open());
        return ParseCsv(await reader.ReadToEndAsync(ct), since);
    }

    /// <summary>Intestazione <c>Date,USD,JPY,…</c>, un giorno per riga, "N/A" per le valute non quotate quel giorno.</summary>
    public static List<ExchangeRate> ParseCsv(string csv, DateOnly since)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0].Split(',');
        var rates = new List<ExchangeRate>();

        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            var date = DateOnly.ParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < since) continue;

            for (var i = 1; i < f.Length && i < header.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(header[i])) continue;
                if (decimal.TryParse(f[i], NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0)
                {
                    rates.Add(new ExchangeRate { Date = date, Currency = header[i].Trim(), UnitsPerEuro = value });
                }
            }
        }

        return rates;
    }
}

/// <summary>
/// Converte importi in euro con il cambio del giorno, o dell'ultimo giorno
/// lavorativo prima, fino a una settimana indietro.
/// </summary>
public class CurrencyConverter
{
    private const int MaxDaysBack = 7;
    private readonly Dictionary<string, SortedList<DateOnly, decimal>> _rates;

    private CurrencyConverter(Dictionary<string, SortedList<DateOnly, decimal>> rates) => _rates = rates;

    public static async Task<CurrencyConverter> LoadAsync(FlarelyticsDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Set<ExchangeRate>().AsNoTracking()
            .Where(r => r.Date >= from.AddDays(-MaxDaysBack) && r.Date <= to)
            .ToListAsync(ct);

        return FromRates(rows);
    }

    public static CurrencyConverter FromRates(IEnumerable<ExchangeRate> rates) => new(rates
        .GroupBy(r => r.Currency.Trim())
        .ToDictionary(g => g.Key, g => new SortedList<DateOnly, decimal>(g.ToDictionary(r => r.Date, r => r.UnitsPerEuro))));

    /// <summary>Se esiste un cambio utilizzabile per quel giorno: serve a non elaborare un report prima che i cambi siano arrivati.</summary>
    public bool HasRatesFor(DateOnly date) => _rates.Count > 0 && _rates.Values.Any(r => Find(r, date) is not null);

    /// <returns>Micro-euro, o null se la BCE non quota quella valuta.</returns>
    public long? ToEurMicros(decimal amount, string currency, DateOnly date)
    {
        if (currency == "EUR") return (long)Math.Round(amount * 1_000_000m);
        if (!_rates.TryGetValue(currency, out var series) || Find(series, date) is not { } rate) return null;

        return (long)Math.Round(amount / rate * 1_000_000m);
    }

    private static decimal? Find(SortedList<DateOnly, decimal> series, DateOnly date)
    {
        for (var d = date; d >= date.AddDays(-MaxDaysBack); d = d.AddDays(-1))
        {
            if (series.TryGetValue(d, out var rate)) return rate;
        }
        return null;
    }
}
