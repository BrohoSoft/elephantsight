using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Sync;

public record SyncSummary(int Downloaded, int Empty, int Processed, string? Error);

/// <summary>
/// Scarica i report di vendita di Apple per una credenziale e li trasforma in
/// metriche.
/// </summary>
/// <remarks>
/// <para><b>Quali giorni.</b> Tutti quelli della finestra di storico che non
/// sono ancora stati scaricati, dal più recente al più vecchio: al primo
/// collegamento l'utente vede subito l'ultima settimana, e l'anno indietro
/// arriva mentre guarda. Più i giorni recenti trovati vuoti, che possono
/// essere solo "non ancora pronti".</para>
///
/// <para>Ogni file si salva appena scaricato: una sincronizzazione interrotta
/// riparte da dove era arrivata.</para>
/// </remarks>
public class AppleSalesSync(
    FlarelyticsDbContext db, IAppleSalesReports apple, CredentialSecrets secrets, ReportStorage storage,
    ReportProcessor processor, IOptions<SyncOptions> options, ILogger<AppleSalesSync> log)
{
    private const int MaxConsecutiveFailures = 3;

    public async Task<SyncSummary> SyncAsync(StoreCredential credential, DateTime nowUtc, CancellationToken ct)
    {
        if (credential.AppleVendorNumber is null)
        {
            const string missing = "Manca il Vendor Number: senza, Apple non fornisce i report di vendita.";
            credential.RecordSync(nowUtc, missing);
            await db.SaveChangesAsync(ct);
            return new SyncSummary(0, 0, 0, missing);
        }

        var (downloaded, empty, error) = await secrets.UseAsync(credential, secret => DownloadAsync(credential, secret, nowUtc, ct), ct);
        var processed = await processor.ProcessPendingAsync(credential, ct);

        credential.RecordSync(DateTime.UtcNow, error);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Sincronizzazione Apple {Credential}: {Downloaded} scaricati, {Empty} vuoti, {Processed} elaborati{Error}",
            credential.Id, downloaded, empty, processed, error is null ? "" : $", errore: {error}");

        return new SyncSummary(downloaded, empty, processed, error);
    }

    private async Task<(int Downloaded, int Empty, string? Error)> DownloadAsync(
        StoreCredential credential, ReadOnlyMemory<byte> secret, DateTime nowUtc, CancellationToken ct)
    {
        var o = options.Value;

        // SKU e Apple ID dall'API: servono ad attribuire gli acquisti in-app
        // anche nei giorni in cui l'app non ha avuto download. Se la chiamata
        // non riesce si va avanti: la corrispondenza si impara anche dai report.
        try
        {
            await ReportProcessor.UpsertSkusAsync(db, credential, await apple.ListAppSkusAsync(credential, secret, ct), ct);
        }
        catch (StoreAccessException e)
        {
            log.LogWarning("Elenco SKU non disponibile per {Credential}: {Message}", credential.Id, e.Message);
        }

        var today = AppleCalendar.Today(nowUtc);
        var oldest = today.AddDays(-o.BackfillDays);
        var retryFrom = today.AddDays(-o.RecentDaysToRetry);

        var files = await db.Set<ReportFile>()
            .Where(f => f.CredentialId == credential.Id && f.Kind == ReportKind.AppleSalesDaily && f.ReportDate >= oldest)
            .ToDictionaryAsync(f => f.ReportDate, ct);

        var dates = Enumerable.Range(1, o.BackfillDays)
            .Select(i => today.AddDays(-i))
            .Where(d => !files.TryGetValue(d, out var f) || (f.Status == ReportFileStatus.Empty && d >= retryFrom))
            .ToList();

        int downloaded = 0, empty = 0, failures = 0;
        string? error = null;

        foreach (var date in dates)
        {
            var result = await apple.FetchDailySalesAsync(credential, secret, date, ct);

            if (!files.TryGetValue(date, out var file))
            {
                file = ReportFile.Create(credential.TenantId, credential.Id, ReportKind.AppleSalesDaily, date);
                db.Add(file);
                files[date] = file;
            }

            switch (result.Outcome)
            {
                case AppleFetchOutcome.Report:
                    var path = await storage.WriteAsync(credential.TenantId, credential.Id, ReportKind.AppleSalesDaily, date, result.Content!, ct);
                    file.MarkStored(path, result.Content!.Length, DateTime.UtcNow);
                    downloaded++;
                    failures = 0;
                    break;

                case AppleFetchOutcome.NoReport:
                    file.MarkEmpty(DateTime.UtcNow);
                    empty++;
                    failures = 0;
                    break;

                case AppleFetchOutcome.Unauthorized:
                case AppleFetchOutcome.Forbidden:
                    // Inutile provare gli altri giorni: fallirebbero tutti allo
                    // stesso modo. Solo il 401 rende la chiave non valida; con
                    // il 403 la chiave funziona, le manca un permesso.
                    if (db.Entry(file).State == EntityState.Added) db.Entry(file).State = EntityState.Detached;
                    credential.RecordVerification(
                        result.Outcome == AppleFetchOutcome.Unauthorized ? CredentialStatus.Invalid : CredentialStatus.Limited,
                        result.Message, DateTime.UtcNow);
                    await db.SaveChangesAsync(ct);
                    return (downloaded, empty, result.Message);

                case AppleFetchOutcome.RateLimited:
                    // Il giorno resta da scaricare: lo riprende il giro successivo.
                    db.Entry(file).State = EntityState.Detached;
                    files.Remove(date);
                    await db.SaveChangesAsync(ct);
                    return (downloaded, empty, null);

                default:
                    db.Entry(file).State = db.Entry(file).State == EntityState.Added ? EntityState.Detached : db.Entry(file).State;
                    files.Remove(date);
                    error = result.Message;
                    if (++failures >= MaxConsecutiveFailures)
                    {
                        await db.SaveChangesAsync(ct);
                        return (downloaded, empty, error);
                    }
                    break;
            }

            await db.SaveChangesAsync(ct);
            if (o.RequestDelay > TimeSpan.Zero) await Task.Delay(o.RequestDelay, ct);
        }

        return (downloaded, empty, failures > 0 ? error : null);
    }
}
