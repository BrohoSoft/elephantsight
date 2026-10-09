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
    BlueskyClient bluesky, MastodonClient mastodon, MetaGraphClient meta, TikTokClient tiktok, ThreadsClient threads, IOptions<SocialOptions> options, ILogger<SocialPublisher> log)
{
    /// <summary>Ogni quanto si ricontrolla un video che la rete sta ancora elaborando.</summary>
    public static readonly TimeSpan ProcessingCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Oltre, l'elaborazione si considera persa: un Reel o un video TikTok ci mettono minuti, non ore.</summary>
    public static readonly TimeSpan MaxProcessing = TimeSpan.FromHours(2);

    /// <summary>La rete sta ancora elaborando: non è un errore, si ricontrolla più tardi.</summary>
    private sealed class StillProcessingException : Exception;

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

        // Il token di Instagram Login o di Threads scaduto non si recupera; quello
        // di TikTok (24 ore) invece si rinnova qui sotto con il token di rinnovo.
        if (account.Network != SocialNetwork.TikTok && account.TokenExpiresAtUtc is { } expires && expires <= DateTime.UtcNow)
        {
            account.MarkBroken(ExpiredMessage(account.Network));
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
                SocialNetwork.Instagram => await PublishInstagramAsync(account, secret, target, post, text, media, ct),
                SocialNetwork.FacebookPage => await PublishFacebookAsync(account, secret, text, media, ct),
                SocialNetwork.TikTok => await PublishTikTokAsync(account, secret, target, post.Options, text, media, ct),
                SocialNetwork.Threads => await PublishThreadsAsync(account, secret, target, text, media, ct),
                _ => throw new SocialApiException("Rete non supportata.")
            };
            target.Published(externalId, url, DateTime.UtcNow);
        }
        catch (StillProcessingException)
        {
            if (target.ProgressStartedAtUtc is { } since && DateTime.UtcNow - since > MaxProcessing)
            {
                target.SetProgress(null);
                target.Fail($"{account.Network} non ha finito di elaborare il video in {MaxProcessing.TotalHours:0} ore: controlla sull'app e riprova.");
            }
            else target.StillProcessing(DateTime.UtcNow + ProcessingCheckInterval);
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
            target.Fail("Un'immagine o il video del post non c'è più sul server.");
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Il token che dura 60 giorni (Instagram Login, Threads) è scaduto: si rifà il login.</summary>
    public static string ExpiredMessage(SocialNetwork network) =>
        $"L'accesso a {(network == SocialNetwork.Threads ? "Threads" : "Instagram")} è scaduto: ricollega l'account.";

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
            var blob = await bluesky.UploadImageAsync(session, await storage.ReadAsync(m, ct), ct);
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
            ids.Add(await mastodon.UploadImageAsync(account.ServerUrl!, token, await storage.ReadAsync(m, ct), m.FileName, m.AltText, ct));
        }

        var (id, url) = await mastodon.PostAsync(account.ServerUrl!, token, text, ids, target.Id.ToString("N"), ct);
        return (id, url);
    }

    /// <summary>
    /// Instagram in tre passi: il container (uno per immagine, più quello del
    /// carosello), l'attesa che Instagram scarichi le immagini, la pubblicazione.
    /// Il container si salva subito, così un riavvio a metà non ne crea un secondo.
    /// </summary>
    private async Task<(string?, string?)> PublishInstagramAsync(SocialAccount account, string token, SocialPostTarget target, SocialPost post, string text,
        List<SocialMedia> media, CancellationToken ct)
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

            if (media is [{ Kind: MediaKind.Video } video])
            {
                // Un video è un Reel. share_to_feed decide se compare anche nella
                // griglia del profilo e nel feed, o solo nella scheda Reel.
                var fields = new List<KeyValuePair<string, string>>
                {
                    new("media_type", "REELS"), new("video_url", PublicImageUrl(video)),
                    new("share_to_feed", post.Options.InstagramShowInGrid ? "true" : "false")
                };
                if (caption is { } c) fields.Add(c);
                container = await meta.CreateInstagramContainerAsync(graph, account.ExternalId, token, fields, ct);
            }
            else if (media.Count == 1)
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

        // Le foto sono pronte in pochi secondi; un Reel può metterci minuti: dopo
        // qualche controllo si lascia stare e si ripassa al giro dopo.
        for (var attempt = 0; ; attempt++)
        {
            var status = await meta.InstagramContainerStatusAsync(graph, container, token, ct);
            if (status is "FINISHED" or null) break;
            if (status is "ERROR" or "EXPIRED")
            {
                target.SetProgress(null);
                throw new SocialApiException("Instagram non è riuscito a scaricare o a usare l'immagine: controlla che l'indirizzo dell'istanza sia raggiungibile da internet.");
            }
            if (attempt == 10) throw new StillProcessingException();
            await Task.Delay(options.Value.PollDelay, ct);
        }

        var mediaId = await meta.PublishInstagramAsync(graph, account.ExternalId, token, container, ct);
        return (mediaId, await meta.InstagramPermalinkAsync(graph, mediaId, token, ct));
    }

    /// <summary>
    /// Threads, come Instagram: il container (uno per elemento, più quello del
    /// carosello), l'attesa che Threads scarichi i file, la pubblicazione. Il
    /// container si salva subito, così un riavvio a metà non ne crea un secondo.
    /// </summary>
    private async Task<(string?, string?)> PublishThreadsAsync(SocialAccount account, string token, SocialPostTarget target, string text,
        List<SocialMedia> media, CancellationToken ct)
    {
        var container = target.ProgressState;
        if (container is not null)
        {
            switch ((await threads.ContainerStatusAsync(container, token, ct)).Status)
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
            KeyValuePair<string, string>[] Item(SocialMedia m) => m.Kind == MediaKind.Video
                ? [new("media_type", "VIDEO"), new("video_url", PublicImageUrl(m))]
                : [new("media_type", "IMAGE"), new("image_url", PublicImageUrl(m))];

            var fields = new List<KeyValuePair<string, string>>();
            if (media.Count == 0) fields.Add(new("media_type", "TEXT"));
            else if (media.Count == 1)
            {
                fields.AddRange(Item(media[0]));
                if (media[0].AltText is { } alt) fields.Add(new("alt_text", alt));
            }
            else
            {
                var children = new List<string>();
                foreach (var m in media)
                {
                    var child = Item(m).Append(new("is_carousel_item", "true")).ToList();
                    if (m.AltText is { } alt) child.Add(new("alt_text", alt));
                    children.Add(await threads.CreateContainerAsync(account.ExternalId, token, child, ct));
                }
                fields.Add(new("media_type", "CAROUSEL"));
                fields.Add(new("children", string.Join(',', children)));
            }
            if (text.Length > 0) fields.Add(new("text", text));

            container = await threads.CreateContainerAsync(account.ExternalId, token, fields, ct);
            target.SetProgress(container);
            await db.SaveChangesAsync(ct);
        }

        // Un testo è pronto subito; immagini e video vanno scaricati da Threads.
        for (var attempt = 0; ; attempt++)
        {
            var (status, error) = await threads.ContainerStatusAsync(container, token, ct);
            if (status is "FINISHED" or null) break;
            if (status is "ERROR" or "EXPIRED")
            {
                target.SetProgress(null);
                throw new SocialApiException($"Threads non è riuscito a usare il post ({error ?? status}): controlla che l'indirizzo dell'istanza sia raggiungibile da internet.");
            }
            if (attempt == 10) throw new StillProcessingException();
            await Task.Delay(options.Value.PollDelay, ct);
        }

        var mediaId = await threads.PublishAsync(account.ExternalId, token, container, ct);
        return (mediaId, await threads.PermalinkAsync(mediaId, token, ct));
    }

    /// <summary>
    /// TikTok: informazioni del creator (obbligatorie, e dicono se adesso può
    /// pubblicare), apertura, caricamento a pezzi, poi si segue lo stato. L'id
    /// della pubblicazione si salva prima di caricare: dopo un riavvio si
    /// riprende da lì invece di pubblicare due volte.
    /// </summary>
    private async Task<(string?, string?)> PublishTikTokAsync(SocialAccount account, string secretJson, SocialPostTarget target, PostOptions choices,
        string text, List<SocialMedia> media, CancellationToken ct)
    {
        var accessToken = await TikTokAccessTokenAsync(account, secretJson, ct);

        if (target.ProgressState is null)
        {
            var video = media is [{ Kind: MediaKind.Video } v] ? v : throw new SocialApiException("Su TikTok si pubblica un video, da solo.");
            var creator = await tiktok.CreatorInfoAsync(accessToken, ct);
            if ((video.DurationMs ?? 0) / 1000 > creator.MaxVideoSeconds)
                throw new SocialApiException($"TikTok: questo account può pubblicare video fino a {creator.MaxVideoSeconds / 60} minuti.");
            if (choices.TikTokPrivacy is not { } privacy || !creator.PrivacyLevels.Contains(privacy))
                throw new SocialApiException($"TikTok: la visibilità scelta non è permessa a questo account (permesse: {string.Join(", ", creator.PrivacyLevels)}).");

            var postInfo = new Dictionary<string, object>
            {
                ["title"] = text,
                ["privacy_level"] = privacy,
                // Disattivato se l'ha chiesto chi pubblica o se il creator l'ha spento nelle sue impostazioni.
                ["disable_comment"] = !choices.TikTokAllowComment || creator.CommentDisabled,
                ["disable_duet"] = !choices.TikTokAllowDuet || creator.DuetDisabled,
                ["disable_stitch"] = !choices.TikTokAllowStitch || creator.StitchDisabled,
                ["brand_organic_toggle"] = choices.TikTokBrandOrganic,
                ["brand_content_toggle"] = choices.TikTokBrandedContent
            };

            await using var file = storage.OpenRead(video);
            var (publishId, uploadUrl) = await tiktok.InitVideoAsync(accessToken, postInfo, file.Length, ct);
            target.SetProgress(publishId);
            await db.SaveChangesAsync(ct);

            try
            {
                await tiktok.UploadAsync(uploadUrl, file, video.ContentType, ct);
            }
            catch
            {
                // Caricamento a metà: TikTok non pubblica niente, al prossimo tentativo si riparte da capo.
                target.SetProgress(null);
                throw;
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var status = await tiktok.StatusAsync(accessToken, target.ProgressState!, ct);
            switch (status.Status)
            {
                case "PUBLISH_COMPLETE":
                    // L'indirizzo c'è solo per i post pubblici già approvati dalla moderazione di TikTok.
                    var url = status.PostId is { } id && account.Handle is { } handle ? $"https://www.tiktok.com/{handle}/video/{id}" : null;
                    return (status.PostId ?? target.ProgressState, url);
                case "FAILED":
                    target.SetProgress(null);
                    throw new SocialApiException($"TikTok non ha pubblicato il video ({status.FailReason ?? "motivo non indicato"}).");
            }
            if (attempt < 4) await Task.Delay(options.Value.PollDelay, ct);
        }
        throw new StillProcessingException();
    }

    /// <summary>Il token d'accesso di TikTok, rinnovato se scade fra meno di 5 minuti (dura 24 ore).</summary>
    private async Task<string> TikTokAccessTokenAsync(SocialAccount account, string secretJson, CancellationToken ct)
    {
        var secret = TikTokSecret.Parse(secretJson);
        if (account.TokenExpiresAtUtc is { } expires && expires > DateTime.UtcNow.AddMinutes(5)) return secret.AccessToken;

        var renewed = await RenewTikTokAsync(account, secret, protector, tiktok, ct);
        await db.SaveChangesAsync(ct);
        return renewed;
    }

    /// <summary>Rinnova il token d'accesso con quello di rinnovo e salva sull'account i token nuovi. Restituisce il token d'accesso.</summary>
    public static async Task<string> RenewTikTokAsync(SocialAccount account, TikTokSecret secret, FieldProtector protector, TikTokClient tiktok, CancellationToken ct)
    {
        if (secret.RefreshExpiresAtUtc <= DateTime.UtcNow)
            throw new SocialApiException("L'accesso a TikTok è scaduto (dura un anno): ricollega l'account.", unauthorized: true);

        var tokens = await tiktok.RefreshAsync(secret.RefreshToken, ct);
        account.RenewToken(protector.Protect(Encoding.UTF8.GetBytes(TikTokSecret.From(tokens).ToJson()), account.SecretContext), tokens.ExpiresAtUtc);
        return tokens.AccessToken;
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

        return publicUrl + signer.PathFor(m, DateTime.UtcNow.AddHours(1));
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

            // Prima le uscite dei post ricorrenti arrivate all'ora: diventano post
            // normali e partono qui sotto, nello stesso giro.
            await scope.ServiceProvider.GetRequiredService<RecurringPostScheduler>().CreateDueAsync(now, ct);

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

    /// <summary>Le immagini caricate nell'editor e mai salvate in un post (né in un post ricorrente).</summary>
    private static async Task DeleteOrphanMediaAsync(FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        var before = DateTime.UtcNow - OrphanMediaAge;
        var orphans = await db.Set<SocialMedia>().Where(m => m.PostId == null && m.RecurringPostId == null && m.CreatedAtUtc < before).ToListAsync(ct);
        if (orphans.Count == 0) return;

        db.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        foreach (var m in orphans) storage.Delete(m);
        storage.DeleteStaleParts(OrphanMediaAge);
    }

    /// <summary>
    /// I token che scadono. Instagram Login e Threads (60 giorni) si rinnovano quando hanno più
    /// di una settimana: Instagram lo permette dopo 24 ore, e così restano
    /// settimane di margine se qualche giro va a vuoto. TikTok (24 ore) quando
    /// ne mancano meno di 2.
    /// </summary>
    private async Task RenewTokensAsync(FlarelyticsDbContext db, IServiceProvider services, CancellationToken ct)
    {
        var renewBefore = DateTime.UtcNow.AddDays(53);
        var expiring = await db.Set<SocialAccount>()
            .Where(a => a.Status == SocialAccountStatus.Connected && a.TokenExpiresAtUtc != null && a.TokenExpiresAtUtc < renewBefore)
            .ToListAsync(ct);
        if (expiring.Count == 0) return;

        var instagram = services.GetRequiredService<InstagramLoginClient>();
        var threads = services.GetRequiredService<ThreadsClient>();
        var tiktok = services.GetRequiredService<TikTokClient>();
        var protector = services.GetRequiredService<FieldProtector>();
        foreach (var account in expiring)
        {
            // TikTok: il token d'accesso dura 24 ore, si rinnova quando ne mancano meno di 2.
            if (account.Network == SocialNetwork.TikTok && account.TokenExpiresAtUtc > DateTime.UtcNow.AddHours(2)) continue;
            if (account.Network != SocialNetwork.TikTok && account.TokenExpiresAtUtc <= DateTime.UtcNow)
            {
                account.MarkBroken(SocialPublisher.ExpiredMessage(account.Network));
                continue;
            }

            var secret = protector.Unprotect(account.ProtectedSecret, account.SecretContext);
            try
            {
                if (account.Network == SocialNetwork.TikTok)
                {
                    await SocialPublisher.RenewTikTokAsync(account, TikTokSecret.Parse(Encoding.UTF8.GetString(secret)), protector, tiktok, ct);
                    continue;
                }
                string accessToken;
                DateTime expiresAtUtc;
                if (account.Network == SocialNetwork.Threads) (accessToken, expiresAtUtc) = await threads.RefreshAsync(Encoding.UTF8.GetString(secret), ct);
                else (accessToken, expiresAtUtc) = await instagram.RefreshAsync(Encoding.UTF8.GetString(secret), ct);
                account.RenewToken(protector.Protect(Encoding.UTF8.GetBytes(accessToken), account.SecretContext), expiresAtUtc);
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
    /// di idempotenza, Instagram e Threads il container salvato); altrove si segna come
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
