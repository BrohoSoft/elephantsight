using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// Threads API (<c>graph.threads.net</c>): login con Threads, poi gli stessi
/// passi di Instagram (container, stato, <c>threads_publish</c>).
/// </summary>
/// <remarks>
/// <para>Come Instagram, Threads non accetta caricamenti: scarica immagini e
/// video da un indirizzo pubblico (vedi <see cref="MediaUrlSigner"/>). Un post
/// di solo testo è un container <c>TEXT</c>.</para>
///
/// <para>Il token: il login ne dà uno di un'ora, che si scambia subito con uno
/// di 60 giorni; il worker lo rinnova prima che scada (si può dopo 24 ore di
/// vita). Uno non rinnovato entro i 60 giorni non si recupera: va rifatto il
/// login. Le credenziali sono quelle del caso d'uso Threads dell'app Meta
/// ("Threads App ID"), diverse da quelle dell'app e da quelle di Instagram.</para>
/// </remarks>
public class ThreadsClient(HttpClient http, IOptions<SocialOptions> options)
{
    public const string Host = "https://graph.threads.net";
    public const string Version = "v1.0";
    public const string Scopes = "threads_basic,threads_content_publish";

    private ThreadsOptions Threads => options.Value.Threads;
    private static string Api => $"{Host}/{Version}";

    public string AuthorizeUrl(string redirectUri, string state) =>
        $"https://threads.net/oauth/authorize?client_id={E(Threads.AppId!)}&redirect_uri={E(redirectUri)}" +
        $"&scope={Scopes}&response_type=code&state={E(state)}";

    public record Token(string AccessToken, DateTime ExpiresAtUtc);

    /// <summary>Codice del login → token di un'ora → token di 60 giorni.</summary>
    public async Task<Token> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        // Come Instagram, Threads può aggiungere "#_" in fondo al codice nell'indirizzo di ritorno.
        if (code.EndsWith("#_")) code = code[..^2];

        var shortLived = await SendAsync(HttpMethod.Post, $"{Host}/oauth/access_token", null, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Threads.AppId!,
            ["client_secret"] = Threads.AppSecret!,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code"] = code
        }), ct);
        var token = shortLived?["access_token"]?.GetValue<string>() ?? throw new SocialApiException("Threads non ha restituito il token di accesso.");

        return Read(await SendAsync(HttpMethod.Get,
            $"{Host}/access_token?grant_type=th_exchange_token&client_secret={E(Threads.AppSecret!)}&access_token={E(token)}", null, null, ct));
    }

    /// <summary>Altri 60 giorni. Il token deve avere almeno 24 ore e non essere scaduto.</summary>
    public async Task<Token> RefreshAsync(string token, CancellationToken ct) =>
        Read(await SendAsync(HttpMethod.Get, $"{Host}/refresh_access_token?grant_type=th_refresh_token&access_token={E(token)}", null, null, ct));

    public record Profile(string Id, string Username, string? Name);

    public async Task<Profile> ProfileAsync(string token, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, $"{Api}/me?fields=id,username,name", token, null, ct);
        return new Profile(
            node?["id"]?.ToString() ?? throw new SocialApiException("Threads non ha restituito l'id dell'account."),
            node["username"]?.GetValue<string>() ?? "",
            node["name"]?.GetValue<string>());
    }

    /// <summary>Un container: TEXT, IMAGE, VIDEO o CAROUSEL (con i figli creati prima, <c>is_carousel_item</c>).</summary>
    public async Task<string> CreateContainerAsync(string userId, string token, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, $"{Api}/{userId}/threads", token, new FormUrlEncodedContent(fields), ct))!["id"]!.GetValue<string>();

    /// <summary>EXPIRED, ERROR, FINISHED, IN_PROGRESS o PUBLISHED, con il motivo se è ERROR.</summary>
    public async Task<(string? Status, string? Error)> ContainerStatusAsync(string containerId, string token, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, $"{Api}/{containerId}?fields=status,error_message", token, null, ct);
        return (node?["status"]?.GetValue<string>(), node?["error_message"]?.GetValue<string>());
    }

    public async Task<string> PublishAsync(string userId, string token, string containerId, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, $"{Api}/{userId}/threads_publish", token,
            new FormUrlEncodedContent([new("creation_id", containerId)]), ct))!["id"]!.GetValue<string>();

    public async Task<string?> PermalinkAsync(string mediaId, string token, CancellationToken ct)
    {
        try
        {
            return (await SendAsync(HttpMethod.Get, $"{Api}/{mediaId}?fields=permalink", token, null, ct))?["permalink"]?.GetValue<string>();
        }
        catch (SocialApiException)
        {
            return null; // il post è uscito: il link è solo una comodità
        }
    }

    /// <summary>
    /// I post dell'account fino a <paramref name="sinceUtc"/>. I repost
    /// (<c>REPOST_FACADE</c>) sono di altri e si saltano; per un video
    /// l'anteprima è <c>thumbnail_url</c>.
    /// </summary>
    public async Task<List<RemotePost>> RecentPostsAsync(string token, DateTime sinceUtc, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        string? next = $"{Api}/me/threads?fields=id,text,media_type,media_url,thumbnail_url,permalink,timestamp&limit=50" +
                       $"&since={new DateTimeOffset(sinceUtc).ToUnixTimeSeconds()}";
        for (var page = 0; next is not null && page < 20; page++)
        {
            var node = await SendAsync(HttpMethod.Get, next, token, null, ct);
            var older = false;
            foreach (var m in node?["data"]?.AsArray() ?? [])
            {
                var at = MetaGraphClient.ParseGraphTime(m!["timestamp"]!.GetValue<string>());
                if (at < sinceUtc) { older = true; continue; }
                var type = m["media_type"]?.GetValue<string>();
                if (type == "REPOST_FACADE") continue;
                var image = type == "VIDEO" ? m["thumbnail_url"] : m["media_url"] ?? m["thumbnail_url"];
                posts.Add(new RemotePost(m["id"]!.GetValue<string>(), m["text"]?.GetValue<string>() ?? "", at,
                    m["permalink"]?.GetValue<string>(), image?.GetValue<string>()));
            }
            next = older ? null : node?["paging"]?["next"]?.GetValue<string>();
        }
        return posts;
    }

    private static Token Read(JsonNode? node) => new(
        node?["access_token"]?.GetValue<string>() ?? throw new SocialApiException("Threads non ha restituito il token di accesso."),
        DateTime.UtcNow.AddSeconds(node["expires_in"]?.GetValue<long>() ?? 5_184_000));

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, string? token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);

        // Lo stesso formato della Graph API: { error: { message, code, error_user_msg, is_transient } }.
        JsonNode? error = null;
        try { error = JsonNode.Parse(text)?["error"]; } catch (JsonException) { }
        var message = error?["error_user_msg"]?.GetValue<string>() ?? error?["message"]?.GetValue<string>();
        var code = error?["code"]?.GetValue<int>();

        if (code == 190) throw new SocialApiException($"Threads non accetta più l'accesso: ricollega l'account. {message}".Trim(), unauthorized: true);
        if (error?["is_transient"]?.GetValue<bool>() == true || code is 4 or 17 or 32 or 613)
            throw new SocialApiException($"Threads: {message ?? "errore temporaneo"}", transient: true);
        throw SocialApiException.From(response.StatusCode, "Threads", message);
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
