using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>Pubblica un post su un account, e ne registra l'esito.</summary>
public class SocialPublisher(
    FlarelyticsDbContext db, FieldProtector protector, SocialMediaStorage storage, MediaUrlSigner signer,
    BlueskyClient bluesky, MastodonClient mastodon, MetaGraphClient meta, IOptions<SocialOptions> options, ILogger<SocialPublisher> log)
{
    /// <summary>Tentativi per gli errori passeggeri, a 2, 4 e 8 minuti di distanza.</summary>
    public const int MaxAttempts = 4;

    public async Task PublishAsync(SocialPostTarget target, SocialPost post, CancellationToken ct)
    {
        var account = target.AccountId is { } accountId ? await db.Set<SocialAccount>().SingleOrDefaultAsync(a => a.Id == accountId, ct) : null;
        if (account is null || account.Status == SocialAccountStatus.NeedsReconnect)
        {
            target.Fail(account is null ? "L'account è stato scollegato." : $"L'account va ricollegato. {account.StatusMessage}".Trim());
            await db.SaveChangesAsync(ct);
            return;
        }

        if (account.TokenExpiresAtUtc is { } expires && expires <= DateTime.UtcNow)
        {
            account.MarkBroken("L'accesso a Instagram è scaduto: ricollega l'account.");
            target.Fail(account.StatusMessage!);
            await db.SaveChangesAsync(ct);
            return;
        }

        target.Start();
        await db.SaveChangesAsync(ct);

        var text = target.TextOverride ?? post.Text;
        var media = post.Media.OrderBy(m => m.Position).ToList();

        try
        {
            var secret = Secret(account);
            var (externalId, url) = account.Network switch
            {
                SocialNetwork.Bluesky => await PublishBlueskyAsync(account, secret, text, media, ct),
                SocialNetwork.Mastodon => await PublishMastodonAsync(account, secret, target, text, media, ct),
                SocialNetwork.Instagram => await PublishInstagramAsync(account, secret, target, text, media, ct),
                SocialNetwork.FacebookPage => await PublishFacebookAsync(account, secret, text, media, ct),
                _ => throw new SocialApiException("Rete non supportata.")
            };
            target.Published(externalId, url, DateTime.UtcNow);
        }
        catch (SocialApiException e) when (e.Unauthorized)
        {
            account.MarkBroken(e.Message);
            target.Fail(e.Message);
        }
        catch (Exception e) when (IsTransient(e, ct))
        {
            var message = e is SocialApiException ? e.Message : $"Rete non raggiungibile: {e.Message}";
            log.LogWarning("Pubblicazione {Target} su {Network} da riprovare: {Message}", target.Id, account.Network, message);
            if (target.Attempts < MaxAttempts) target.RetryLater(message, DateTime.UtcNow.AddMinutes(Math.Pow(2, target.Attempts)));
            else target.Fail(message);
        }
        catch (SocialApiException e)
        {
            log.LogWarning("Pubblicazione {Target} su {Network} rifiutata: {Message}", target.Id, account.Network, e.Message);
            target.Fail(e.Message);
        }
        catch (FileNotFoundException)
        {
            target.Fail("Un'immagine del post non c'è più sul server.");
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsTransient(Exception e, CancellationToken ct) => e switch
    {
        SocialApiException s => s.Transient,
        HttpRequestException => true,
        TaskCanceledException => !ct.IsCancellationRequested, // il timeout del client, non lo spegnimento
        _ => false
    };

    private string Secret(SocialAccount account)
    {
        var bytes = protector.Unprotect(account.ProtectedSecret, account.SecretContext);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<(string?, string?)> PublishBlueskyAsync(SocialAccount account, string password, string text, List<SocialMedia> media, CancellationToken ct)
    {
        var session = await bluesky.LoginAsync(account.ServerUrl ?? BlueskyClient.DefaultService, account.Handle ?? account.ExternalId, password, ct);

        var images = new List<(System.Text.Json.Nodes.JsonNode, string?, int, int)>();
        foreach (var m in media)
        {
            var blob = await bluesky.UploadImageAsync(session, await storage.ReadAsync(m.TenantId, m.Id, ct), ct);
            images.Add((blob, m.AltText, m.Width, m.Height));
        }

        var (uri, url) = await bluesky.CreatePostAsync(session, text, images, DateTime.UtcNow, ct);
        return (uri, url);
    }

    private async Task<(string?, string?)> PublishMastodonAsync(SocialAccount account, string token, SocialPostTarget target, string text, List<SocialMedia> media, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var m in media)
        {
            ids.Add(await mastodon.UploadImageAsync(account.ServerUrl!, token, await storage.ReadAsync(m.TenantId, m.Id, ct), m.FileName, m.AltText, ct));
        }

        var (id, url) = await mastodon.PostAsync(account.ServerUrl!, token, text, ids, target.Id.ToString("N"), ct);
        return (id, url);
    }

    /// <summary>
    /// Instagram in tre passi: il container (uno per immagine, più quello del
    /// carosello), l'attesa che Instagram scarichi le immagini, la pubblicazione.
    /// Il container si salva subito, così un riavvio a metà non ne crea un secondo.
    /// </summary>
    private async Task<(string?, string?)> PublishInstagramAsync(SocialAccount account, string token, SocialPostTarget target, string text, List<SocialMedia> media, CancellationToken ct)
    {
        var graph = meta.InstagramBase(account);
        var container = target.ProgressState;
        if (container is not null)
        {
            switch (await meta.InstagramContainerStatusAsync(graph, container, token, ct))
            {
                case "PUBLISHED":
                    return (null, null); // era uscito prima dell'interruzione
                case "EXPIRED" or "ERROR":
                    container = null;
                    break;
            }
        }

        if (container is null)
        {
            var caption = text.Length > 0 ? new KeyValuePair<string, string>("caption", text) : (KeyValuePair<string, string>?)null;

            if (media.Count == 1)
            {
                var fields = new List<KeyValuePair<string, string>> { new("image_url", PublicImageUrl(media[0])) };
                if (caption is { } c) fields.Add(c);
                if (media[0].AltText is { } alt) fields.Add(new("alt_text", alt));
                container = await meta.CreateInstagramContainerAsync(graph, account.ExternalId, token, fields, ct);
            }
            else
            {
                // Il testo alternativo è documentato solo per i post con
                // un'immagine: nel carosello non lo si manda, per non farsi
                // rifiutare tutto il post da un campo.
                var children = new List<string>();
                foreach (var m in media)
                {
                    children.Add(await meta.CreateInstagramContainerAsync(graph, account.ExternalId, token,
                        [new("image_url", PublicImageUrl(m)), new("is_carousel_item", "true")], ct));
                }

                var fields = new List<KeyValuePair<string, string>> { new("media_type", "CAROUSEL"), new("children", string.Join(',', children)) };
                if (caption is { } c) fields.Add(c);
                container = await meta.CreateInstagramContainerAsync(graph, account.ExternalId, token, fields, ct);
            }

            target.SetProgress(container);
            await db.SaveChangesAsync(ct);
        }

        for (var attempt = 0; ; attempt++)
        {
            var status = await meta.InstagramContainerStatusAsync(graph, container, token, ct);
            if (status is "FINISHED" or null) break;
            if (status is "ERROR" or "EXPIRED")
            {
                target.SetProgress(null);
                throw new SocialApiException("Instagram non è riuscito a scaricare o a usare l'immagine: controlla che l'indirizzo dell'istanza sia raggiungibile da internet.");
            }
            if (attempt == 20) throw new SocialApiException("Instagram sta ancora elaborando il post: si riprova più tardi.", transient: true);
            await Task.Delay(options.Value.PollDelay, ct);
        }

        var mediaId = await meta.PublishInstagramAsync(graph, account.ExternalId, token, container, ct);
        return (mediaId, await meta.InstagramPermalinkAsync(graph, mediaId, token, ct));
    }

    private async Task<(string?, string?)> PublishFacebookAsync(SocialAccount account, string token, string text, List<SocialMedia> media, CancellationToken ct)
    {
        string postId;
        if (media.Count == 0)
        {
            postId = await meta.PostPageTextAsync(account.ExternalId, token, text, ct);
        }
        else if (media.Count == 1)
        {
            var (photoId, post) = await meta.PostPagePhotoAsync(account.ExternalId, token, PublicImageUrl(media[0]), text, published: true, ct);
            postId = post ?? photoId;
        }
        else
        {
            // Più foto: si caricano nascoste e poi si attaccano a un post solo.
            var photos = new List<string>();
            foreach (var m in media)
            {
                photos.Add((await meta.PostPagePhotoAsync(account.ExternalId, token, PublicImageUrl(m), null, published: false, ct)).PhotoId);
            }
            postId = await meta.PostPageWithPhotosAsync(account.ExternalId, token, text, photos, ct);
        }

        return (postId, $"https://www.facebook.com/{postId}");
    }

    /// <summary>
    /// L'indirizzo da cui Meta scarica l'immagine: firmato, valido un'ora.
    /// Un indirizzo locale non funzionerebbe, e l'errore di Meta non lo direbbe.
    /// </summary>
    private string PublicImageUrl(SocialMedia m)
    {
        var publicUrl = options.Value.PublicUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(publicUrl) || Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost"))
            throw new SocialApiException($"Meta scarica le immagini dall'indirizzo pubblico dell'istanza, e '{publicUrl}' non è raggiungibile da internet: imposta PUBLIC_URL.");

        return publicUrl + signer.PathFor(m.TenantId, m.Id, DateTime.UtcNow.AddHours(1));
    }
}

/// <summary>
/// Il ciclo della pubblicazione: ogni 15 secondi cerca i post arrivati alla
/// loro ora e li manda, tenant per tenant come gli altri worker.
/// </summary>
public class SocialPublishWorker(IServiceScopeFactory scopes, ILogger<SocialPublishWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan OrphanMediaAge = TimeSpan.FromDays(1);

    private DateTime _lastCleanup = DateTime.MinValue;

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
                log.LogError(e, "Giro della pubblicazione sui social non riuscito");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cleanup = DateTime.UtcNow - _lastCleanup >= TimeSpan.FromHours(1);
        if (cleanup) _lastCleanup = DateTime.UtcNow;

        foreach (var tenant in await TenantsAsync(ct))
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<SocialPublisher>();
            var now = DateTime.UtcNow;

            var due = await db.Set<SocialPost>().Include(p => p.Media).Include(p => p.Targets)
                .Where(p => !p.IsDraft && p.ScheduledAtUtc <= now)
                .Where(p => p.Targets.Any(t => t.Status == SocialTargetStatus.Pending && (t.NextAttemptAtUtc == null || t.NextAttemptAtUtc <= now)))
                .OrderBy(p => p.ScheduledAtUtc)
                .ToListAsync(ct);

            foreach (var post in due)
            {
                foreach (var target in post.Targets.Where(t => t.Status == SocialTargetStatus.Pending && (t.NextAttemptAtUtc == null || t.NextAttemptAtUtc <= now)).ToList())
                {
                    await publisher.PublishAsync(target, post, ct);
                }
            }

            // Dopo la pubblicazione, così un post appena uscito non si importa due volte.
            await scope.ServiceProvider.GetRequiredService<SocialImporter>().ImportDueAsync(DateTime.UtcNow, ct);

            if (cleanup)
            {
                await DeleteOrphanMediaAsync(db, scope.ServiceProvider.GetRequiredService<SocialMediaStorage>(), ct);
                await RenewTokensAsync(db, scope.ServiceProvider, ct);
            }
        }
    }

    /// <summary>Le immagini caricate nell'editor e mai salvate in un post.</summary>
    private static async Task DeleteOrphanMediaAsync(FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        var before = DateTime.UtcNow - OrphanMediaAge;
        var orphans = await db.Set<SocialMedia>().Where(m => m.PostId == null && m.CreatedAtUtc < before).ToListAsync(ct);
        if (orphans.Count == 0) return;

        db.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        foreach (var m in orphans) storage.Delete(m.TenantId, m.Id);
    }

    /// <summary>
    /// I token che scadono (Instagram Login, 60 giorni) si rinnovano quando
    /// hanno più di una settimana: Instagram lo permette dopo 24 ore, e così
    /// restano settimane di margine se qualche giro va a vuoto.
    /// </summary>
    private async Task RenewTokensAsync(FlarelyticsDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var renewBefore = DateTime.UtcNow.AddDays(53);
        var expiring = await db.Set<SocialAccount>()
            .Where(a => a.Status == SocialAccountStatus.Connected && a.TokenExpiresAtUtc != null && a.TokenExpiresAtUtc < renewBefore)
            .ToListAsync(ct);
        if (expiring.Count == 0) return;

        var instagram = services.GetRequiredService<InstagramLoginClient>();
        var protector = services.GetRequiredService<FieldProtector>();
        foreach (var account in expiring)
        {
            if (account.TokenExpiresAtUtc <= DateTime.UtcNow)
            {
                account.MarkBroken("L'accesso a Instagram è scaduto: ricollega l'account.");
                continue;
            }

            var secret = protector.Unprotect(account.ProtectedSecret, account.SecretContext);
            try
            {
                var token = await instagram.RefreshAsync(Encoding.UTF8.GetString(secret), ct);
                account.RenewToken(protector.Protect(Encoding.UTF8.GetBytes(token.AccessToken), account.SecretContext), token.ExpiresAtUtc);
            }
            catch (SocialApiException e) when (e.Unauthorized)
            {
                account.MarkBroken(e.Message);
            }
            catch (Exception e) when (e is SocialApiException or HttpRequestException)
            {
                log.LogWarning("Rinnovo del token di {Account} non riuscito, si riprova fra un'ora: {Message}", account.Id, e.Message);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Guid>> TenantsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<Tenant>().Select(t => t.Id).ToListAsync(ct);
    }

    /// <summary>
    /// All'avvio: un post rimasto "in pubblicazione" da un riavvio a metà si
    /// riprova solo dove ripetere non crea un doppione (Mastodon ha la chiave
    /// di idempotenza, Instagram il container salvato); altrove si segna come
    /// fallito e decide l'utente.
    /// </summary>
    public override async Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var tenant in await TenantsAsync(ct))
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
                var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();

                var interrupted = await db.Set<SocialPostTarget>().Where(t => t.Status == SocialTargetStatus.Publishing).ToListAsync(ct);
                foreach (var t in interrupted) t.Interrupted(t.Network == SocialNetwork.Mastodon || t.ProgressState is not null);
                if (interrupted.Count > 0) await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Ripristino delle pubblicazioni interrotte non riuscito");
        }

        await base.StartAsync(ct);
    }
}
