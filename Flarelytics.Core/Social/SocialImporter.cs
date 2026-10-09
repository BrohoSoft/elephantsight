using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Social;

/// <summary>Un post già uscito su una rete, letto dalla sua API.</summary>
/// <param name="ImageUrl">L'anteprima, sul CDN della rete: va scaricata subito, gli indirizzi scadono.</param>
public record RemotePost(string ExternalId, string Text, DateTime PublishedAtUtc, string? Url, string? ImageUrl);

/// <summary>Scarica le anteprime dei post importati. Solo https, solo JPEG, al massimo 8 MB.</summary>
public class RemoteImageClient(HttpClient http)
{
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Null se l'immagine non c'è, è troppo grande o non è un JPEG: il post si importa lo stesso, senza.</summary>
    public async Task<byte[]?> DownloadJpegAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return JpegInfo.TryReadSize(bytes, out _, out _) ? bytes : null;
    }
}

/// <summary>
/// Porta sul calendario i post pubblicati fuori da ElephantSight: da Business
/// Suite, dall'app, dal sito. Ogni account si rilegge ogni mezz'ora; la prima
/// volta si va indietro di 90 giorni, poi si riparte dall'ultima lettura (con un
/// giorno di sovrapposizione, i doppioni si riconoscono dall'id).
/// </summary>
/// <remarks>
/// I post programmati in Business Suite e non ancora usciti non ci sono: Meta
/// non li espone via API.
/// </remarks>
public class SocialImporter(
    FlarelyticsDbContext db, FieldProtector protector, SocialMediaStorage storage, RemoteImageClient images,
    BlueskyClient bluesky, MastodonClient mastodon, MetaGraphClient meta, ThreadsClient threads, ILogger<SocialImporter> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public const int HistoryDays = 90;

    public async Task ImportDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = nowUtc - Interval;
        var accounts = await db.Set<SocialAccount>()
            .Where(a => a.Status == SocialAccountStatus.Connected && (a.LastImportAtUtc == null || a.LastImportAtUtc < due))
            .ToListAsync(ct);

        foreach (var account in accounts)
        {
            try
            {
                await ImportAsync(account, nowUtc, ct);
            }
            catch (SocialApiException e) when (e.Unauthorized)
            {
                account.MarkBroken(e.Message);
            }
            catch (Exception e) when (e is SocialApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Si riprova al giro dopo: l'importazione è una comodità, non deve fermare niente.
                log.LogWarning("Importazione dei post di {Account} non riuscita: {Message}", account.Id, e.Message);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Legge i post recenti dell'account e aggiunge al calendario quelli che non ci sono. Restituisce quanti.</summary>
    public async Task<int> ImportAsync(SocialAccount account, DateTime nowUtc, CancellationToken ct)
    {
        var since = account.LastImportAtUtc is { } last ? last.AddDays(-1) : nowUtc.AddDays(-HistoryDays);
        var remote = account.Network switch
        {
            // Bluesky: i post sono pubblici, non serve la password.
            SocialNetwork.Bluesky => await bluesky.RecentPostsAsync(account.ExternalId, since, ct),
            SocialNetwork.Mastodon => await mastodon.RecentPostsAsync(account.ServerUrl!, Secret(account), account.ExternalId.Split('@')[0], since, ct),
            SocialNetwork.Instagram => await meta.InstagramRecentPostsAsync(meta.InstagramBase(account), account.ExternalId, Secret(account), since, ct),
            SocialNetwork.FacebookPage => await meta.PageRecentPostsAsync(account.ExternalId, Secret(account), since, ct),
            SocialNetwork.Threads => await threads.RecentPostsAsync(Secret(account), since, ct),
            _ => []
        };

        var ids = remote.Select(p => p.ExternalId).ToList();
        var known = (await db.Set<SocialPostTarget>()
            .Where(t => t.Network == account.Network && t.ExternalId != null && ids.Contains(t.ExternalId))
            .Select(t => t.ExternalId!).ToListAsync(ct)).ToHashSet();

        var added = 0;
        foreach (var item in remote.Where(p => !known.Contains(p.ExternalId)).DistinctBy(p => p.ExternalId))
        {
            var text = item.Text.Length > 10000 ? item.Text[..10000] : item.Text;
            var post = SocialPost.Imported(account.TenantId, text, item.PublishedAtUtc, account.CreatedByUserId);
            db.Add(post);

            var target = SocialPostTarget.For(post, account);
            target.Published(item.ExternalId, item.Url is { Length: <= 500 } url ? url : null, item.PublishedAtUtc);
            post.AddTarget(target);
            db.Add(target);

            if (item.ImageUrl is { } imageUrl && await TryDownloadAsync(imageUrl, ct) is { } jpeg && JpegInfo.TryReadSize(jpeg, out var w, out var h))
            {
                var media = SocialMedia.Create(account.TenantId, "anteprima.jpg", jpeg.Length, w, h, account.CreatedByUserId);
                await storage.WriteAsync(media, jpeg, ct);
                media.AttachTo(post.Id, 0, null);
                post.AddMedia(media);
                db.Add(media);
            }
            added++;
        }

        account.MarkImported(nowUtc);
        if (added > 0) log.LogInformation("Importati {Count} post di {Account} ({Network})", added, account.Id, account.Network);
        return added;
    }

    private async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            return await images.DownloadJpegAsync(url, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // senza anteprima, ma il post si importa
        }
    }

    private string Secret(SocialAccount account)
    {
        var bytes = protector.Unprotect(account.ProtectedSecret, account.SecretContext);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
