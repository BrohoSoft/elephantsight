using System.Security.Cryptography;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Management;

/// <summary>Dove aspettano le build caricate dal pannello, prima di andare allo store.</summary>
public class UploadStorage(IOptions<ReportsOptions> options)
{
    public string Root => Path.Combine(options.Value.StorageDirectory, "_uploads");

    public string PathFor(Guid tenantId, Guid uploadId, string extension)
    {
        var directory = Path.Combine(Root, tenantId.ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, uploadId.ToString("N") + extension);
    }

    public void Delete(string? path)
    {
        if (path is not null && File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Porta una build dallo spazio di attesa allo store.</summary>
public class BuildUploader(FlarelyticsDbContext db, AppleApi apple, GooglePublisher google, CredentialSecrets secrets,
    UploadStorage storage, ILogger<BuildUploader> log)
{
    /// <summary>Un caricamento in coda: lo manda allo store. Per Apple si ferma a "in elaborazione".</summary>
    public async Task SendAsync(BuildUpload upload, CancellationToken ct)
    {
        var credential = await db.Set<StoreCredential>().SingleOrDefaultAsync(c => c.Id == upload.CredentialId, ct);
        if (credential is null)
        {
            upload.Fail("La chiave con cui era collegata l'app non c'è più.", DateTime.UtcNow);
            await FinishAsync(upload, ct);
            return;
        }

        upload.Start();
        await db.SaveChangesAsync(ct);

        try
        {
            await using var file = File.OpenRead(upload.StoragePath!);
            await secrets.UseAsync(credential, async secret =>
            {
                if (upload.Store == Store.GooglePlay) await SendGoogleAsync(upload, secret, file, ct);
                else await SendAppleAsync(upload, apple.Open(credential, secret), file, ct);
                return true;
            }, ct);
        }
        catch (Exception e) when (e is StoreAccessException or HttpRequestException or IOException)
        {
            log.LogWarning("Caricamento {Upload} non riuscito: {Message}", upload.Id, e.Message);
            upload.Fail(e is StoreAccessException ? e.Message : $"Caricamento interrotto: {e.Message}", DateTime.UtcNow);
        }

        await FinishAsync(upload, ct);
    }

    /// <summary>
    /// Google: tutto in un edit. Si carica il bundle, si mette la release nel
    /// canale con stato, percentuale e note, e si conferma. Se qualcosa va
    /// storto l'edit si butta e su Google Play non cambia niente.
    /// </summary>
    private async Task SendGoogleAsync(BuildUpload upload, ReadOnlyMemory<byte> secret, Stream file, CancellationToken ct)
    {
        var session = await google.OpenAsync(secret, ct);
        var versionCode = await session.WithEditAsync(upload.AppId, commit: true, async edit =>
        {
            var bundle = await session.UploadAsync($"{GooglePublisher.UploadApp(upload.AppId)}/edits/{edit}/bundles?uploadType=media",
                file, "application/octet-stream", ct);
            var code = bundle?["versionCode"]?.ToString() ?? throw new StoreAccessException("Google non ha restituito il versionCode del bundle.");

            var release = new Dictionary<string, object> { ["versionCodes"] = new[] { code }, ["status"] = upload.ReleaseStatus! };
            if (upload.Version is { Length: > 0 } name) release["name"] = name;
            if (upload.ReleaseStatus == "inProgress" && upload.RolloutPercent is { } percent) release["userFraction"] = percent / 100.0;
            if (upload.ReleaseNotes is { Length: > 0 } notes)
                release["releaseNotes"] = new[] { new { language = upload.ReleaseNotesLanguage ?? "it-IT", text = notes } };

            await session.SendJsonAsync(HttpMethod.Put, $"{GooglePublisher.App(upload.AppId)}/edits/{edit}/tracks/{Uri.EscapeDataString(upload.Track!)}",
                new { track = upload.Track, releases = new[] { release } }, ct);
            return code;
        }, ct);

        upload.Complete(versionCode, $"Pubblicata nel canale {upload.Track}.", DateTime.UtcNow);
    }

    /// <summary>
    /// Apple, API "Build Uploads" (App Store Connect API 4.1): si crea il
    /// caricamento, poi il file con le sue <c>uploadOperations</c>, si mandano i
    /// pezzi e si conferma con l'MD5. L'elaborazione la segue <see cref="CheckAppleAsync"/>.
    /// </summary>
    private static async Task SendAppleAsync(BuildUpload upload, AppleSession session, Stream file, CancellationToken ct)
    {
        var created = await session.CreateAsync("buildUploads",
            new { cfBundleShortVersionString = upload.Version, cfBundleVersion = upload.BuildNumber, platform = "IOS" },
            new Dictionary<string, (string, string)> { ["app"] = ("apps", upload.AppId) }, ct);
        var uploadId = created!["data"]!.Id();

        var reservation = await session.CreateAsync("buildUploadFiles",
            new { assetType = "ASSET", fileName = upload.FileName, fileSize = file.Length, uti = "com.apple.ipa" },
            new Dictionary<string, (string, string)> { ["buildUpload"] = ("buildUploads", uploadId) }, ct);
        var fileResource = reservation!["data"]!;

        await session.UploadAsync(fileResource["attributes"]?["uploadOperations"], file, ct);

        file.Seek(0, SeekOrigin.Begin);
        var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(file, ct));
        await session.UpdateAsync("buildUploadFiles", fileResource.Id(),
            new { uploaded = true, sourceFileChecksums = new { file = new { hash = md5, algorithm = "MD5" } } }, ct);

        upload.Processing(uploadId);
    }

    /// <summary>Segue l'elaborazione di Apple: un caricamento resta "in elaborazione" finché Apple non dice com'è andata.</summary>
    public async Task CheckAppleAsync(BuildUpload upload, CancellationToken ct)
    {
        var credential = await db.Set<StoreCredential>().SingleOrDefaultAsync(c => c.Id == upload.CredentialId, ct);
        if (credential is null) return;

        try
        {
            var state = await secrets.UseAsync(credential, async secret =>
                (await apple.Open(credential, secret).GetAsync($"v1/buildUploads/{upload.ExternalId}", ct))?["data"]?["attributes"]?["state"], ct);

            switch (state?["state"]?.GetValue<string>())
            {
                case "COMPLETE":
                    upload.Complete(null, "Elaborata da Apple: la trovi fra le build e in TestFlight.", DateTime.UtcNow);
                    break;
                case "FAILED":
                    var errors = state["errors"]?.AsArray().Select(e => e?["description"]?.GetValue<string>()).OfType<string>() ?? [];
                    upload.Fail("Apple ha rifiutato la build: " + string.Join(" ", errors), DateTime.UtcNow);
                    break;
                default:
                    return; // ancora in elaborazione: si ricontrolla al prossimo giro
            }
        }
        catch (Exception e) when (e is StoreAccessException or HttpRequestException)
        {
            log.LogWarning("Stato del caricamento {Upload} non disponibile: {Message}", upload.Id, e.Message);
            return;
        }

        await FinishAsync(upload, ct);
    }

    private async Task FinishAsync(BuildUpload upload, CancellationToken ct)
    {
        // Il file serve finché si può doverlo rimandare: dopo un esito finale,
        // o una volta consegnato ad Apple, si libera il disco.
        if (upload.Status is BuildUploadStatus.Completed or BuildUploadStatus.Failed or BuildUploadStatus.Processing)
        {
            storage.Delete(upload.StoragePath);
            upload.ForgetFile();
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Il ciclo dei caricamenti, separato da quello della sincronizzazione: una
/// build caricata deve partire subito, non al prossimo giro dei report.
/// </summary>
public class BuildUploadWorker(IServiceScopeFactory scopes, ILogger<BuildUploadWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AppleCheckInterval = TimeSpan.FromMinutes(1);

    private DateTime _lastAppleCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Giro dei caricamenti non riuscito");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Tenant per tenant, come la sincronizzazione: la Row-Level Security vale anche qui.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var checkApple = DateTime.UtcNow - _lastAppleCheck >= AppleCheckInterval;
        if (checkApple) _lastAppleCheck = DateTime.UtcNow;

        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<Tenant>().Select(t => t.Id).ToListAsync(ct);
        }

        foreach (var tenant in tenants)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            var uploader = scope.ServiceProvider.GetRequiredService<BuildUploader>();

            // Un caricamento alla volta per tenant, dal più vecchio.
            var queued = await db.Set<BuildUpload>().Where(u => u.Status == BuildUploadStatus.Queued).OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (queued is not null) await uploader.SendAsync(queued, ct);

            if (!checkApple) continue;
            foreach (var processing in await db.Set<BuildUpload>().Where(u => u.Status == BuildUploadStatus.Processing).ToListAsync(ct))
            {
                await uploader.CheckAppleAsync(processing, ct);
            }
        }
    }

    /// <summary>
    /// All'avvio: un caricamento rimasto "in invio" da un riavvio a metà si
    /// rimette in coda, se il file c'è ancora.
    /// </summary>
    public override async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            var tenants = await db.Set<Tenant>().Select(t => t.Id).ToListAsync(ct);
            foreach (var tenant in tenants)
            {
                await using var tenantScope = scopes.CreateAsyncScope();
                tenantScope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
                var tenantDb = tenantScope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
                await tenantDb.Set<BuildUpload>().Where(u => u.Status == BuildUploadStatus.Uploading)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, BuildUploadStatus.Queued), ct);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Ripristino dei caricamenti interrotti non riuscito");
        }

        await base.StartAsync(ct);
    }
}
