using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Sync;

/// <summary>
/// Scarica dal bucket di Play Console i report mensili delle installazioni e
/// li trasforma in metriche.
/// </summary>
/// <remarks>
/// <para>Google riscrive ogni giorno il file del mese in corso (e per qualche
/// giorno anche quello del mese prima). Il file si riscarica solo quando il
/// suo MD5 cambia: un giro senza novità costa una sola richiesta, l'elenco.</para>
///
/// <para>Il report di un'app contiene solo quell'app: a differenza di Apple
/// non serve sapere quali app ha l'account, basta leggere i nomi dei file.</para>
/// </remarks>
public class GooglePlaySync(
    FlarelyticsDbContext db, IGooglePlayReports google, CredentialSecrets secrets, ReportStorage storage,
    IOptions<SyncOptions> options, ILogger<GooglePlaySync> log)
{
    public async Task<SyncSummary> SyncAsync(StoreCredential credential, DateTime nowUtc, CancellationToken ct)
    {
        if (credential.GoogleReportsBucket is null)
        {
            const string missing = "Manca il bucket dei report: senza, installazioni e guadagni non si possono scaricare.";
            credential.RecordSync(nowUtc, missing);
            await db.SaveChangesAsync(ct);
            return new SyncSummary(0, 0, 0, missing);
        }

        var (downloaded, error) = await secrets.UseAsync(credential, secret => DownloadAsync(credential, secret, nowUtc, ct), ct);
        var processed = await ProcessPendingAsync(credential, ct);

        credential.RecordSync(DateTime.UtcNow, error);
        await db.SaveChangesAsync(ct);

        log.LogInformation("Sincronizzazione Google Play {Credential}: {Downloaded} file scaricati, {Processed} elaborati{Error}",
            credential.Id, downloaded, processed, error is null ? "" : $", errore: {error}");

        return new SyncSummary(downloaded, 0, processed, error);
    }

    private async Task<(int Downloaded, string? Error)> DownloadAsync(
        StoreCredential credential, ReadOnlyMemory<byte> secret, DateTime nowUtc, CancellationToken ct)
    {
        var bucket = credential.GoogleReportsBucket!;
        var today = DateOnly.FromDateTime(nowUtc);
        var oldest = today.AddDays(-options.Value.BackfillDays);
        var since = new DateOnly(oldest.Year, oldest.Month, 1);
        var downloaded = 0;

        try
        {
            var token = await google.ConnectAsync(secret, ct);

            // Solo i report delle installazioni: i report finanziari di Google
            // (vendite e guadagni) non si leggono, per scelta.
            var objects = new List<(BucketObject Object, ReportKind Kind, DateOnly Month, string Scope)>();

            foreach (var o in await google.ListAsync(token, bucket, GoogleInstallsParser.Prefix, ct))
            {
                if (GoogleInstallsParser.ParseName(o.Name) is { } p) objects.Add((o, ReportKind.GooglePlayInstallsMonthly, p.Month, p.Package));
            }

            var files = await db.Set<ReportFile>()
                .Where(f => f.CredentialId == credential.Id && f.Kind != ReportKind.AppleSalesDaily && f.ReportDate >= since)
                .ToDictionaryAsync(f => (f.Kind, f.Scope, f.ReportDate), ct);

            // Dal mese più recente: chi collega l'account vede subito i dati nuovi.
            foreach (var (obj, kind, month, scope) in objects.Where(x => x.Month >= since).OrderByDescending(x => x.Month))
            {
                if (files.TryGetValue((kind, scope, month), out var file) && file.ContentHash == obj.Md5) continue;

                var content = await google.DownloadAsync(token, bucket, obj.Name, ct);
                var path = await storage.WriteAsync(credential.TenantId, credential.Id, kind, obj.Name, content, ct);

                if (file is null)
                {
                    file = ReportFile.Create(credential.TenantId, credential.Id, kind, month, scope);
                    db.Add(file);
                    files[(kind, scope, month)] = file;
                }

                file.MarkStored(path, content.Length, DateTime.UtcNow, obj.Md5);
                await db.SaveChangesAsync(ct);
                downloaded++;

                if (options.Value.RequestDelay > TimeSpan.Zero) await Task.Delay(options.Value.RequestDelay, ct);
            }

            // Arrivati fin qui, il bucket si legge: se la chiave era segnata
            // come parziale per un invito non ancora attivo, adesso non lo è più.
            if (credential.Status == CredentialStatus.Limited)
            {
                credential.RecordVerification(CredentialStatus.Valid, null, DateTime.UtcNow);
            }

            return (downloaded, null);
        }
        catch (GoogleAccessException e)
        {
            if (e.Problem != GoogleAccessProblem.Failed)
            {
                credential.RecordVerification(
                    e.Problem == GoogleAccessProblem.InvalidKey ? CredentialStatus.Invalid : CredentialStatus.Limited,
                    e.Message, DateTime.UtcNow);
            }
            await db.SaveChangesAsync(ct);
            return (downloaded, e.Message);
        }
    }

    /// <summary>
    /// Elabora i file non ancora elaborati, o elaborati da una versione vecchia
    /// del parser. Non usa la rete.
    /// </summary>
    public async Task<int> ProcessPendingAsync(StoreCredential credential, CancellationToken ct)
    {
        var processed = 0;

        var installs = await Pending(credential, ReportKind.GooglePlayInstallsMonthly, GoogleInstallsParser.Version).ToListAsync(ct);
        foreach (var file in installs)
        {
            await ProcessInstallsAsync(credential, file, ct);
            processed++;
        }

        return processed;
    }

    private IQueryable<ReportFile> Pending(StoreCredential credential, ReportKind kind, int version) =>
        db.Set<ReportFile>().Where(f => f.CredentialId == credential.Id && f.Kind == kind && f.Status == ReportFileStatus.Stored
                                        && (f.ProcessedAtUtc == null || f.ParserVersion < version));

    /// <summary>
    /// Riscrive le colonne delle installazioni per l'app e il mese del file.
    /// </summary>
    /// <remarks>
    /// Solo quelle colonne, non la riga intera: le stesse righe riceveranno i
    /// ricavi da un altro report di Google, che non deve essere cancellato da
    /// questo. Le righe del mese che il file non contiene più si azzerano.
    /// </remarks>
    private async Task ProcessInstallsAsync(StoreCredential credential, ReportFile file, CancellationToken ct)
    {
        var parsed = GoogleInstallsParser.Parse(credential.TenantId, file.Scope, await storage.ReadAsync(file.RelativePath!, ct));
        var monthEnd = file.ReportDate.AddMonths(1).AddDays(-1);

        var existing = await db.Set<DailyAppMetric>()
            .Where(m => m.Store == Store.GooglePlay && m.AppId == file.Scope && m.Date >= file.ReportDate && m.Date <= monthEnd)
            .ToDictionaryAsync(m => (m.Date, m.CountryCode), ct);

        foreach (var row in existing.Values)
        {
            row.Downloads = 0;
            row.Updates = 0;
            row.Uninstalls = 0;
        }

        foreach (var m in parsed.Where(m => m.Date >= file.ReportDate && m.Date <= monthEnd))
        {
            if (existing.TryGetValue((m.Date, m.CountryCode), out var row))
            {
                row.Downloads = m.Downloads;
                row.Updates = m.Updates;
                row.Uninstalls = m.Uninstalls;
            }
            else
            {
                db.Add(m);
                existing[(m.Date, m.CountryCode)] = m;
            }
        }

        file.MarkProcessed(GoogleInstallsParser.Version, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        foreach (var m in existing.Values) db.Entry(m).State = EntityState.Detached;
    }
}
