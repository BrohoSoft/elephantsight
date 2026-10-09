using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// Mastodon, con un token creato dall'utente sulla sua istanza (Preferenze →
/// Sviluppo → Nuova applicazione, permessi <c>read:accounts</c>,
/// <c>write:statuses</c>, <c>write:media</c>). Niente OAuth: ogni istanza è un
/// server diverso, e il token incollato funziona con tutte.
/// </summary>
public class MastodonClient(HttpClient http, IOptions<SocialOptions> options)
{
    public record Account(string Id, string Username, string Name);

    public async Task<Account> VerifyAsync(string instance, string token, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get, $"{instance}/api/v1/accounts/verify_credentials", token, null, ct);
        var name = node!["display_name"]?.GetValue<string>();
        var username = node["username"]!.GetValue<string>();
        return new Account(node["id"]!.GetValue<string>(), username, string.IsNullOrWhiteSpace(name) ? username : name);
    }

    /// <summary>Il limite di caratteri dell'istanza; null se l'istanza non lo dice.</summary>
    public async Task<int?> MaxCharactersAsync(string instance, CancellationToken ct)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, $"{instance}/api/v2/instance", null, null, ct);
            return node?["configuration"]?["statuses"]?["max_characters"]?.GetValue<int>();
        }
        catch (SocialApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Carica un'immagine. Le piccole tornano pronte (200); le altre (202) si
    /// elaborano in background e si aspettano, perché un post con un allegato
    /// non pronto viene rifiutato.
    /// </summary>
    public async Task<string> UploadImageAsync(string instance, string token, byte[] jpeg, string fileName, string? description, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", fileName);
        if (!string.IsNullOrWhiteSpace(description)) form.Add(new StringContent(description), "description");

        var (status, node) = await SendWithStatusAsync(HttpMethod.Post, $"{instance}/api/v2/media", token, form, ct);
        var id = node!["id"]!.GetValue<string>();

        for (var attempt = 0; status == HttpStatusCode.Accepted || status == HttpStatusCode.PartialContent; attempt++)
        {
            if (attempt == 20) throw new SocialApiException("Mastodon: l'istanza non ha finito di elaborare l'immagine.", transient: true);
            await Task.Delay(options.Value.PollDelay, ct);
            (status, _) = await SendWithStatusAsync(HttpMethod.Get, $"{instance}/api/v1/media/{id}", token, null, ct);
        }

        return id;
    }

    /// <summary>
    /// Pubblica. L'<c>Idempotency-Key</c> è l'id del post sull'account: se il
    /// worker riprova dopo un'interruzione, Mastodon restituisce lo stesso post
    /// invece di crearne un secondo.
    /// </summary>
    public async Task<(string Id, string? Url)> PostAsync(string instance, string token, string text, IReadOnlyList<string> mediaIds, string idempotencyKey, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>> { new("status", text), new("visibility", "public") };
        fields.AddRange(mediaIds.Select(id => new KeyValuePair<string, string>("media_ids[]", id)));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{instance}/api/v1/statuses") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        var (_, node) = await SendAsync(request, token, ct);
        return (node!["id"]!.GetValue<string>(), node["url"]?.GetValue<string>());
    }

    /// <summary>I post dell'account dal più recente, fino a <paramref name="sinceUtc"/>, senza risposte né condivisioni.</summary>
    public async Task<List<RemotePost>> RecentPostsAsync(string instance, string token, string accountId, DateTime sinceUtc, CancellationToken ct)
    {
        var posts = new List<RemotePost>();
        string? maxId = null;
        for (var page = 0; page < 20; page++)
        {
            var node = await SendAsync(HttpMethod.Get,
                $"{instance}/api/v1/accounts/{Uri.EscapeDataString(accountId)}/statuses?exclude_replies=true&exclude_reblogs=true&limit=40" +
                (maxId is null ? "" : $"&max_id={maxId}"), token, null, ct);
            var statuses = node?.AsArray() ?? [];
            if (statuses.Count == 0) break;

            var older = false;
            foreach (var status in statuses)
            {
                var at = DateTime.Parse(status!["created_at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
                if (at < sinceUtc) { older = true; continue; }
                var image = status["media_attachments"]?.AsArray().FirstOrDefault(m => m?["type"]?.GetValue<string>() == "image");
                posts.Add(new RemotePost(status["id"]!.GetValue<string>(), PlainText(status["content"]?.GetValue<string>() ?? ""), at,
                    status["url"]?.GetValue<string>(), image?["preview_url"]?.GetValue<string>()));
            }

            maxId = statuses[^1]!["id"]!.GetValue<string>();
            if (older) break;
        }
        return posts;
    }

    /// <summary>Mastodon restituisce il testo in HTML: paragrafi e a capo diventano righe, il resto si toglie.</summary>
    internal static string PlainText(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>|</p>\s*<p>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, string? token, HttpContent? content, CancellationToken ct) =>
        (await SendWithStatusAsync(method, uri, token, content, ct)).Node;

    private async Task<(HttpStatusCode Status, JsonNode? Node)> SendWithStatusAsync(HttpMethod method, string uri, string? token, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        return await SendAsync(request, token, ct);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Node)> SendAsync(HttpRequestMessage request, string? token, CancellationToken ct)
    {
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));

        string? message = null;
        try { message = JsonNode.Parse(text)?["error"]?.GetValue<string>(); } catch (JsonException) { }
        throw SocialApiException.From(response.StatusCode, "Mastodon", message);
    }
}
