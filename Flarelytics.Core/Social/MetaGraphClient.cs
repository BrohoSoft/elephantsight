using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// Graph API di Meta: Pagine Facebook e account Instagram professionali, con
/// un solo login ("Facebook Login for Business").
/// </summary>
/// <remarks>
/// <para>Si parte dal token dell'utente, lo si allunga (60 giorni) e da lì si
/// leggono le Pagine con i loro token: un token di Pagina ottenuto da un token
/// utente lungo non scade, quindi non c'è un rinnovo da tenere in piedi.
/// L'account Instagram si usa con il token della Pagina a cui è collegato.</para>
///
/// <para>Instagram non accetta caricamenti: scarica l'immagine da un URL
/// pubblico (vedi <see cref="MediaUrlSigner"/>). Lo stesso URL va bene per le
/// foto delle Pagine.</para>
/// </remarks>
public class MetaGraphClient(HttpClient http, IOptions<SocialOptions> options)
{
    /// <summary>
    /// Pagine (elenco, pubblicazione) e Instagram (lettura, pubblicazione).
    /// business_management serve per le Pagine gestite da un Business Manager.
    /// </summary>
    public const string Scopes = "pages_show_list,pages_read_engagement,pages_manage_posts,instagram_basic,instagram_content_publish,business_management";

    private MetaOptions Meta => options.Value.Meta;
    private string Graph => $"https://graph.facebook.com/{Meta.GraphVersion}";

    public string AuthorizeUrl(string redirectUri, string state) =>
        $"https://www.facebook.com/{Meta.GraphVersion}/dialog/oauth?client_id={Uri.EscapeDataString(Meta.AppId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}&response_type=code&scope={Scopes}";

    /// <summary>Codice del login → token utente → token utente lungo.</summary>
    public async Task<string> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var shortLived = await SendAsync(HttpMethod.Get,
            $"{Graph}/oauth/access_token?client_id={E(Meta.AppId!)}&client_secret={E(Meta.AppSecret!)}&redirect_uri={E(redirectUri)}&code={E(code)}", null, null, ct);
        var longLived = await SendAsync(HttpMethod.Get,
            $"{Graph}/oauth/access_token?grant_type=fb_exchange_token&client_id={E(Meta.AppId!)}&client_secret={E(Meta.AppSecret!)}" +
            $"&fb_exchange_token={E(shortLived!["access_token"]!.GetValue<string>())}", null, null, ct);
        return longLived!["access_token"]!.GetValue<string>();
    }

    public record PageInfo(string Id, string Name, string Token, string? InstagramId, string? InstagramUsername);

    /// <summary>Le Pagine che l'utente ha autorizzato, ciascuna con il suo token e l'eventuale account Instagram collegato.</summary>
    public async Task<List<PageInfo>> ListPagesAsync(string userToken, CancellationToken ct)
    {
        var pages = new List<PageInfo>();
        string? next = $"{Graph}/me/accounts?fields=id,name,access_token,instagram_business_account{{id,username}}&limit=100";

        while (next is not null)
        {
            var node = await SendAsync(HttpMethod.Get, next, userToken, null, ct);
            foreach (var p in node?["data"]?.AsArray() ?? [])
            {
                var ig = p!["instagram_business_account"];
                pages.Add(new PageInfo(p["id"]!.GetValue<string>(), p["name"]!.GetValue<string>(), p["access_token"]!.GetValue<string>(),
                    ig?["id"]?.GetValue<string>(), ig?["username"]?.GetValue<string>()));
            }
            next = node?["paging"]?["next"]?.GetValue<string>();
        }

        return pages;
    }

    // --- Pagine Facebook ---

    public async Task<string> PostPageTextAsync(string pageId, string token, string message, CancellationToken ct) =>
        (await SendFormAsync($"{Graph}/{pageId}/feed", token, [new("message", message)], ct))!["id"]!.GetValue<string>();

    /// <summary>Una foto. Con <paramref name="published"/> falso resta nascosta, pronta per un post con più foto.</summary>
    public async Task<(string PhotoId, string? PostId)> PostPagePhotoAsync(string pageId, string token, string imageUrl, string? message, bool published, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>> { new("url", imageUrl), new("published", published ? "true" : "false") };
        if (!string.IsNullOrEmpty(message)) fields.Add(new("message", message));
        var node = await SendFormAsync($"{Graph}/{pageId}/photos", token, fields, ct);
        return (node!["id"]!.GetValue<string>(), node["post_id"]?.GetValue<string>());
    }

    public async Task<string> PostPageWithPhotosAsync(string pageId, string token, string message, IReadOnlyList<string> photoIds, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>> { new("message", message) };
        fields.AddRange(photoIds.Select((id, i) => new KeyValuePair<string, string>($"attached_media[{i}]", JsonSerializer.Serialize(new { media_fbid = id }))));
        return (await SendFormAsync($"{Graph}/{pageId}/feed", token, fields, ct))!["id"]!.GetValue<string>();
    }

    // --- Instagram ---
    // Gli stessi passi valgono per i due login: cambia solo l'host.
    // Collegato tramite Facebook: graph.facebook.com con il token della
    // Pagina. Con Instagram Login: graph.instagram.com con il token dell'utente.

    /// <summary>L'indirizzo base della Graph API per quell'account Instagram.</summary>
    public string InstagramBase(Database.Entities.SocialAccount account) =>
        account.ServerUrl is { Length: > 0 } host ? $"{host}/{Meta.GraphVersion}" : Graph;

    public async Task<string> CreateInstagramContainerAsync(string graphBase, string igId, string token, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct) =>
        (await SendFormAsync($"{graphBase}/{igId}/media", token, fields, ct))!["id"]!.GetValue<string>();

    /// <summary>EXPIRED, ERROR, FINISHED, IN_PROGRESS o PUBLISHED.</summary>
    public async Task<string?> InstagramContainerStatusAsync(string graphBase, string containerId, string token, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Get, $"{graphBase}/{containerId}?fields=status_code", token, null, ct))?["status_code"]?.GetValue<string>();

    public async Task<string> PublishInstagramAsync(string graphBase, string igId, string token, string containerId, CancellationToken ct) =>
        (await SendFormAsync($"{graphBase}/{igId}/media_publish", token, [new("creation_id", containerId)], ct))!["id"]!.GetValue<string>();

    public async Task<string?> InstagramPermalinkAsync(string graphBase, string mediaId, string token, CancellationToken ct)
    {
        try
        {
            return (await SendAsync(HttpMethod.Get, $"{graphBase}/{mediaId}?fields=permalink", token, null, ct))?["permalink"]?.GetValue<string>();
        }
        catch (SocialApiException)
        {
            return null; // il post è uscito: il link è solo una comodità
        }
    }

    /// <summary>
    /// I post dell'account Instagram fino a <paramref name="sinceUtc"/>. Per un
    /// video l'anteprima è <c>thumbnail_url</c>; per un carosello
    /// <c>media_url</c> è la prima immagine.
    /// </summary>
    public async Task<List<RemotePost>> InstagramRecentPostsAsync(string graphBase, string igId, string token, DateTime sinceUtc, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        string? next = $"{graphBase}/{igId}/media?fields=id,caption,media_type,media_url,thumbnail_url,permalink,timestamp&limit=50" +
                       $"&since={new DateTimeOffset(sinceUtc).ToUnixTimeSeconds()}";
        for (var page = 0; next is not null && page < 20; page++)
        {
            var node = await SendAsync(HttpMethod.Get, next, token, null, ct);
            var older = false;
            foreach (var m in node?["data"]?.AsArray() ?? [])
            {
                var at = ParseGraphTime(m!["timestamp"]!.GetValue<string>());
                if (at < sinceUtc) { older = true; continue; }
                var image = m["media_type"]?.GetValue<string>() == "VIDEO" ? m["thumbnail_url"] : m["media_url"];
                posts.Add(new RemotePost(m["id"]!.GetValue<string>(), m["caption"]?.GetValue<string>() ?? "", at,
                    m["permalink"]?.GetValue<string>(), image?.GetValue<string>()));
            }
            next = older ? null : node?["paging"]?["next"]?.GetValue<string>();
        }
        return posts;
    }

    /// <summary>I post pubblicati della Pagina fino a <paramref name="sinceUtc"/>.</summary>
    public async Task<List<RemotePost>> PageRecentPostsAsync(string pageId, string token, DateTime sinceUtc, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        string? next = $"{Graph}/{pageId}/published_posts?fields=id,message,created_time,permalink_url,full_picture&limit=50" +
                       $"&since={new DateTimeOffset(sinceUtc).ToUnixTimeSeconds()}";
        for (var page = 0; next is not null && page < 20; page++)
        {
            var node = await SendAsync(HttpMethod.Get, next, token, null, ct);
            var older = false;
            foreach (var p in node?["data"]?.AsArray() ?? [])
            {
                var at = ParseGraphTime(p!["created_time"]!.GetValue<string>());
                if (at < sinceUtc) { older = true; continue; }
                posts.Add(new RemotePost(p["id"]!.GetValue<string>(), p["message"]?.GetValue<string>() ?? "", at,
                    p["permalink_url"]?.GetValue<string>(), p["full_picture"]?.GetValue<string>()));
            }
            next = older ? null : node?["paging"]?["next"]?.GetValue<string>();
        }
        return posts;
    }

    /// <summary>La Graph API scrive le date come "2026-10-09T08:30:00+0000", senza i due punti nel fuso.</summary>
    internal static DateTime ParseGraphTime(string value) =>
        DateTimeOffset.Parse(System.Text.RegularExpressions.Regex.Replace(value, @"([+-]\d{2})(\d{2})$", "$1:$2"),
            System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    /// <summary>
    /// I messaggi d'errore in italiano. Senza, Meta li traduce nella lingua che
    /// ricava dalla posizione del server (in un datacenter di Francoforte
    /// arrivavano in tedesco). Vale per Graph API, Instagram e Threads.
    /// </summary>
    internal static string WithLocale(string uri) =>
        uri.Contains("locale=") ? uri : uri + (uri.Contains('?') ? "&" : "?") + "locale=it_IT";

    private Task<JsonNode?> SendFormAsync(string uri, string token, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, uri, token, new FormUrlEncodedContent(fields), ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, string? token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, WithLocale(uri)) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);

        JsonNode? error = null;
        try { error = JsonNode.Parse(text)?["error"]; } catch (JsonException) { }
        var message = error?["error_user_msg"]?.GetValue<string>() ?? error?["message"]?.GetValue<string>();

        // 190: token scaduto o revocato (password cambiata, app rimossa dalla Pagina).
        if (error?["code"]?.GetValue<int>() == 190)
            throw new SocialApiException($"Meta non accetta più l'accesso: ricollega l'account. {message}".Trim(), unauthorized: true);
        // 4, 17, 32, 613: limiti di frequenza. is_transient: Meta stessa dice di riprovare.
        if (error?["is_transient"]?.GetValue<bool>() == true || error?["code"]?.GetValue<int>() is 4 or 17 or 32 or 613)
            throw new SocialApiException($"Meta: {message ?? "errore temporaneo"}", transient: true);

        throw SocialApiException.From(response.StatusCode, "Meta", message);
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
