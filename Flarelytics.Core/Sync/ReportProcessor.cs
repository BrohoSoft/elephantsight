using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Sync;

/// <summary>
/// Dai report salvati alle metriche. Non parla con gli store: legge i file,
/// quindi rielaborare tutto (per un parser corretto, per un cambio arrivato in
/// ritardo) non costa richieste ad Apple.
/// </summary>
/// <remarks>Lavora sul tenant già impostato nel <see cref="Tenancy.TenantContext"/> dello scope.</remarks>
public class ReportProcessor(FlarelyticsDbContext db, ReportStorage storage, ILogger<ReportProcessor> log)
{
    /// <summary>Elabora i file della credenziale mai elaborati, o elaborati da una versione vecchia del parser.</summary>
    /// <returns>Quanti file sono stati elaborati.</returns>
    public async Task<int> ProcessPendingAsync(StoreCredential credential, CancellationToken ct)
    {
        var pending = await db.Set<ReportFile>()
            .Where(f => f.CredentialId == credential.Id && f.Kind == ReportKind.AppleSalesDaily && f.Status == ReportFileStatus.Stored
                        && (f.ProcessedAtUtc == null || f.ParserVersion < AppleSalesAggregator.Version))
            .OrderBy(f => f.ReportDate)
            .ToListAsync(ct);

        if (pending.Count == 0) return 0;

        var converter = await CurrencyConverter.LoadAsync(db, pending[0].ReportDate, pending[^1].ReportDate, ct);
        var processed = 0;

        foreach (var file in pending)
        {
            // Senza cambi per quel giorno i ricavi non si possono convertire:
            // si aspetta il giro successivo invece di salvare totali sbagliati.
            if (!converter.HasRatesFor(file.ReportDate))
            {
                log.LogInformation("Report del {Date} rimandato: nessun cambio BCE ancora disponibile", file.ReportDate);
                continue;
            }

            await ProcessAsync(credential, file, converter, ct);
            processed++;
        }

        return processed;
    }

    private async Task ProcessAsync(StoreCredential credential, ReportFile file, CurrencyConverter converter, CancellationToken ct)
    {
        var rows = AppleSalesParser.Parse(await storage.ReadAsync(file.RelativePath!, ct));
        await LearnSkusAsync(credential, rows, ct);

        var skus = await db.Set<AppleAppSku>().AsNoTracking().ToDictionaryAsync(s => s.Sku, s => s.AppleId, ct);
        var (metrics, unattributed) = AppleSalesAggregator.Aggregate(
            credential.TenantId, file.ReportDate, rows, skus,
            (amount, currency) => converter.ToEurMicros(amount, currency, file.ReportDate));

        if (unattributed > 0)
        {
            log.LogWarning("Report del {Date}: {Rows} righe di acquisti in-app senza un'app riconoscibile", file.ReportDate, unattributed);
        }

        // Si sostituiscono le righe di quel giorno per le app dell'account,
        // non solo per quelle presenti nel report: se una rielaborazione
        // trova un'app in meno, la riga vecchia non deve restare.
        var accountApps = await db.Set<AppleAppSku>()
            .Where(s => s.CredentialId == credential.Id)
            .Select(s => s.AppleId)
            .ToListAsync(ct);
        var apps = accountApps.Concat(metrics.Select(m => m.AppId)).Distinct().ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Set<DailyAppMetric>()
            .Where(m => m.Store == Store.AppStore && m.Date == file.ReportDate && apps.Contains(m.AppId))
            .ExecuteDeleteAsync(ct);

        db.AddRange(metrics);
        file.MarkProcessed(AppleSalesAggregator.Version, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Le righe appena inserite non servono più in memoria: con un anno di
        // storico sarebbero centinaia di migliaia di entità tracciate. Solo
        // quelle: la credenziale e i file devono restare tracciati, il
        // chiamante li salva ancora.
        foreach (var metric in metrics) db.Entry(metric).State = EntityState.Detached;
    }

    /// <summary>Le righe di download portano SKU e Apple ID insieme: è la corrispondenza che serve agli acquisti in-app.</summary>
    private async Task LearnSkusAsync(StoreCredential credential, List<AppleSalesRow> rows, CancellationToken ct)
    {
        var seen = rows
            .Where(r => AppleSalesAggregator.IsAppRow(r.ProductType) && r.Sku.Length > 0 && r.AppleIdentifier.Length > 0)
            .Select(r => (r.Sku, r.AppleIdentifier))
            .Distinct()
            .ToList();

        await UpsertSkusAsync(db, credential, seen, ct);
    }

    public static async Task UpsertSkusAsync(FlarelyticsDbContext db, StoreCredential credential, IEnumerable<(string Sku, string AppleId)> pairs, CancellationToken ct)
    {
        var existing = await db.Set<AppleAppSku>().ToDictionaryAsync(s => s.Sku, ct);

        foreach (var (sku, appleId) in pairs)
        {
            if (existing.TryGetValue(sku, out var row))
            {
                row.AppleId = appleId;
                row.CredentialId = credential.Id;
            }
            else
            {
                var added = new AppleAppSku { TenantId = credential.TenantId, CredentialId = credential.Id, Sku = sku, AppleId = appleId };
                db.Add(added);
                existing[sku] = added;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
