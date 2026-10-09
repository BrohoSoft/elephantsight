using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Social;

/// <summary>
/// Un errore di una rete social. Deriva da <see cref="StoreAccessException"/>
/// perché l'API lo tratti allo stesso modo (502 con il messaggio della rete).
/// </summary>
/// <param name="transient">Vale la pena riprovare più tardi (rete giù, limite di frequenza, errore 5xx).</param>
/// <param name="unauthorized">Il token o la password non valgono più: l'account va ricollegato.</param>
public class SocialApiException(string message, bool transient = false, bool unauthorized = false) : StoreAccessException(message)
{
    public bool Transient { get; } = transient;
    public bool Unauthorized { get; } = unauthorized;

    /// <summary>Come la rete ha risposto, nelle tre categorie che contano per il worker.</summary>
    public static SocialApiException From(HttpStatusCode status, string network, string? message) => (int)status switch
    {
        401 => new($"{network} non accetta più le credenziali dell'account: ricollegalo. {message}".Trim(), unauthorized: true),
        429 => new($"{network}: troppe richieste, si riprova più tardi.", transient: true),
        >= 500 => new($"{network} non risponde ({(int)status}). {message}".Trim(), transient: true),
        _ => new($"{network}: {message ?? $"errore {(int)status}"}")
    };
}

/// <summary>
/// Bluesky (AT Protocol), con una password per app: si crea una sessione,
/// si caricano le immagini come blob e si scrive il record del post.
/// </summary>
/// <remarks>
/// La password per app (Impostazioni → Privacy e sicurezza → Password per le
/// app) non è quella dell'account e si revoca da sola. La sessione si apre a
/// ogni pubblicazione: i token durano poco e non vale la pena conservarli.
/// </remarks>
public class BlueskyClient(HttpClient http)
{
    public const string DefaultService = "https://bsky.social";

    public record Session(string Service, string AccessJwt, string Did, string Handle);

    public async Task<Session> LoginAsync(string service, string identifier, string password, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Post, $"{service}/xrpc/com.atproto.server.createSession", null,
            JsonContent.Create(new { identifier, password }), ct, loginFailure: true);
        return new Session(service, node!["accessJwt"]!.GetValue<string>(), node["did"]!.GetValue<string>(), node["handle"]!.GetValue<string>());
    }

    /// <summary>Restituisce il blob da mettere nel post così com'è.</summary>
    public async Task<JsonNode> UploadImageAsync(Session session, byte[] jpeg, CancellationToken ct)
    {
        var body = new ByteArrayContent(jpeg);
        body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var node = await SendAsync(HttpMethod.Post, $"{session.Service}/xrpc/com.atproto.repo.uploadBlob", session.AccessJwt, body, ct);
        return node!["blob"]!.DeepClone();
    }

    /// <summary>Scrive il post. Restituisce l'uri at:// e l'indirizzo su bsky.app.</summary>
    public async Task<(string Uri, string Url)> CreatePostAsync(Session session, string text, IReadOnlyList<(JsonNode Blob, string? Alt, int Width, int Height)> images,
        DateTime nowUtc, CancellationToken ct)
    {
        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = text,
            ["createdAt"] = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        var facets = Facets(text);
        if (facets.Count > 0) record["facets"] = facets;

        if (images.Count > 0)
        {
            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.images",
                ["images"] = new JsonArray(images.Select(i => (JsonNode)new JsonObject
                {
                    ["alt"] = i.Alt ?? "",
                    ["image"] = i.Blob.DeepClone(),
                    ["aspectRatio"] = new JsonObject { ["width"] = i.Width, ["height"] = i.Height }
                }).ToArray())
            };
        }

        var node = await SendAsync(HttpMethod.Post, $"{session.Service}/xrpc/com.atproto.repo.createRecord", session.AccessJwt,
            JsonContent.Create(new JsonObject { ["repo"] = session.Did, ["collection"] = "app.bsky.feed.post", ["record"] = record }), ct);

        var uri = node!["uri"]!.GetValue<string>();
        return (uri, $"https://bsky.app/profile/{session.Did}/post/{uri[(uri.LastIndexOf('/') + 1)..]}");
    }

    /// <summary>L'AppView pubblica di Bluesky: i post sono pubblici, non serve una sessione per leggerli.</summary>
    public const string PublicAppView = "https://public.api.bsky.app";

    /// <summary>
    /// I post dell'account dal più recente, fino a <paramref name="sinceUtc"/>.
    /// Niente risposte né repost: nel calendario vanno solo i post suoi.
    /// </summary>
    public async Task<List<RemotePost>> RecentPostsAsync(string did, DateTime sinceUtc, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var node = await SendAsync(HttpMethod.Get,
                $"{PublicAppView}/xrpc/app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(did)}&filter=posts_no_replies&limit=50" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), null, null, ct);

            var older = false;
            foreach (var item in node?["feed"]?.AsArray() ?? [])
            {
                var post = item!["post"]!;
                if (item["reason"] is not null || post["author"]?["did"]?.GetValue<string>() != did) continue; // repost
                var at = DateTime.Parse(post["record"]?["createdAt"]?.GetValue<string>() ?? post["indexedAt"]!.GetValue<string>(),
                    null, System.Globalization.DateTimeStyles.AdjustToUniversal);
                if (at < sinceUtc) { older = true; continue; }

                var uri = post["uri"]!.GetValue<string>();
                posts.Add(new RemotePost(uri, post["record"]?["text"]?.GetValue<string>() ?? "", at,
                    $"https://bsky.app/profile/{did}/post/{uri[(uri.LastIndexOf('/') + 1)..]}",
                    post["embed"]?["images"]?[0]?["thumb"]?.GetValue<string>()));
            }

            cursor = node?["cursor"]?.GetValue<string>();
            if (older || cursor is null) break;
        }
        return posts;
    }

    /// <summary>
    /// Link e hashtag cliccabili. Bluesky non li riconosce da solo nel testo:
    /// vanno indicati come "facet", con gli estremi in byte UTF-8 (non in caratteri).
    /// </summary>
    public static JsonArray Facets(string text)
    {
        var facets = new JsonArray();

        foreach (System.Text.RegularExpressions.Match m in SocialRules.Url().Matches(text))
        {
            // La punteggiatura in fondo ("vedi https://x.it.") di solito non è del link.
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '"', '\'');
            facets.Add(Facet(text, m.Index, url.Length, new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = url }));
        }

        foreach (System.Text.RegularExpressions.Match m in SocialRules.Hashtag().Matches(text))
        {
            var tag = m.Value[1..];
            if (tag.All(char.IsDigit)) continue; // "#1" non è un hashtag
            facets.Add(Facet(text, m.Index, m.Length, new JsonObject { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = tag }));
        }

        return facets;
    }

    private static JsonObject Facet(string text, int index, int length, JsonObject feature)
    {
        var start = Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
        return new JsonObject
        {
            ["index"] = new JsonObject { ["byteStart"] = start, ["byteEnd"] = start + Encoding.UTF8.GetByteCount(text.AsSpan(index, length)) },
            ["features"] = new JsonArray(feature)
        };
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, string? token, HttpContent? content, CancellationToken ct, bool loginFailure = false)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);

        string? message = null;
        try { message = JsonNode.Parse(text)?["message"]?.GetValue<string>(); } catch (JsonException) { }

        // Una password sbagliata al login è un 401: la si dice così.
        if (loginFailure && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new SocialApiException("Bluesky non accetta handle e password per app: controllali, e usa una password per app, non quella dell'account.", unauthorized: true);

        throw SocialApiException.From(response.StatusCode, "Bluesky", message);
    }
}
