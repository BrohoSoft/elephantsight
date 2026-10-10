using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// TikTok, Content Posting API ("Direct Post"): login con TikTok, poi il video
/// si carica a pezzi e TikTok lo pubblica sul profilo.
/// </summary>
/// <remarks>
/// <para>Il token d'accesso dura 24 ore, quello di rinnovo un anno: si
/// conservano tutti e due (cifrati) e il worker rinnova il primo prima che
/// scada. Il video si manda con FILE_UPLOAD, non da un indirizzo: con
/// PULL_FROM_URL TikTok vorrebbe un dominio verificato.</para>
///
/// <para>Finché l'app non ha passato la revisione di TikTok, i post escono solo
/// privati, su account privati, per al massimo 5 utenti al giorno: è una
/// regola di TikTok, e i suoi errori lo dicono.</para>
/// </remarks>
public class TikTokClient(HttpClient http, IOptionsMonitor<SocialOptions> options)
{
    public const string Api = "https://open.tiktokapis.com";
    public const string Scopes = "user.info.basic,video.publish";

    private TikTokOptions TikTok => options.CurrentValue.TikTok;

    public string AuthorizeUrl(string redirectUri, string state) =>
        $"https://www.tiktok.com/v2/auth/authorize/?client_key={E(TikTok.ClientKey!)}&scope={E(Scopes)}" +
        $"&response_type=code&redirect_uri={E(redirectUri)}&state={E(state)}";

    /// <param name="RefreshExpiresAtUtc">Oltre, non si rinnova più: va rifatto il login.</param>
    public record Tokens(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken, DateTime RefreshExpiresAtUtc, string OpenId);

    public Task<Tokens> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct) => TokenAsync(new Dictionary<string, string>
    {
        ["client_key"] = TikTok.ClientKey!, ["client_secret"] = TikTok.ClientSecret!, ["code"] = code,
        ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri
    }, ct);

    /// <summary>Il token di rinnovo può cambiare a ogni rinnovo: va salvato quello nuovo.</summary>
    public Task<Tokens> RefreshAsync(string refreshToken, CancellationToken ct) => TokenAsync(new Dictionary<string, string>
    {
        ["client_key"] = TikTok.ClientKey!, ["client_secret"] = TikTok.ClientSecret!, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
    }, ct);

    private async Task<Tokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync($"{Api}/v2/oauth/token/", new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch (JsonException) { }

        // Gli errori dell'OAuth arrivano anche con 200: { "error": "invalid_grant", "error_description": … }.
        if (!response.IsSuccessStatusCode || node?["error"] is JsonValue { } error && error.ToString().Length > 0 || node?["access_token"] is null)
        {
            var description = node?["error_description"]?.GetValue<string>() ?? node?["error"]?.ToString();
            if (response.StatusCode >= HttpStatusCode.InternalServerError) throw SocialApiException.From(response.StatusCode, "TikTok", description);
            throw new SocialApiException($"TikTok non accetta più l'accesso: ricollega l'account. {description}".Trim(), unauthorized: true);
        }

        var now = DateTime.UtcNow;
        return new Tokens(
            node["access_token"]!.GetValue<string>(), now.AddSeconds(node["expires_in"]?.GetValue<long>() ?? 86400),
            node["refresh_token"]!.GetValue<string>(), now.AddSeconds(node["refresh_expires_in"]?.GetValue<long>() ?? 31_536_000),
            node["open_id"]?.GetValue<string>() ?? "");
    }

    /// <param name="MaxVideoSeconds">La durata massima che quel creator può pubblicare (TikTok la decide per account).</param>
    public record CreatorInfo(string Username, string Nickname, IReadOnlyList<string> PrivacyLevels,
        bool CommentDisabled, bool DuetDisabled, bool StitchDisabled, int MaxVideoSeconds);

    /// <summary>
    /// Chi è il creator e cosa può fare adesso. TikTok la vuole prima di ogni
    /// pubblicazione, e il pannello la mostra mentre si prepara il post.
    /// Se in questo momento l'account non può pubblicare, TikTok risponde con un errore.
    /// </summary>
    public async Task<CreatorInfo> CreatorInfoAsync(string accessToken, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, $"{Api}/v2/post/publish/creator_info/query/", accessToken, JsonContent.Create(new { }), ct);
        return new CreatorInfo(
            data?["creator_username"]?.GetValue<string>() ?? "",
            data?["creator_nickname"]?.GetValue<string>() ?? "",
            data?["privacy_level_options"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
            data?["comment_disabled"]?.GetValue<bool>() ?? false,
            data?["duet_disabled"]?.GetValue<bool>() ?? false,
            data?["stitch_disabled"]?.GetValue<bool>() ?? false,
            data?["max_video_post_duration_sec"]?.GetValue<int>() ?? 600);
    }

    /// <summary>
    /// I pezzi del caricamento secondo le regole di TikTok: fino a 64 MB un
    /// pezzo solo; oltre, pezzi da 10 MB, e l'ultimo si prende anche il resto.
    /// </summary>
    public static (long ChunkSize, int Count) Chunks(long size)
    {
        const long Single = 64L * 1024 * 1024, Chunk = 10_000_000;
        return size <= Single ? (size, 1) : (Chunk, (int)(size / Chunk));
    }

    /// <summary>Apre la pubblicazione: restituisce l'id da seguire e l'indirizzo a cui mandare il file.</summary>
    public async Task<(string PublishId, string UploadUrl)> InitVideoAsync(string accessToken, object postInfo, long size, CancellationToken ct)
    {
        var (chunkSize, count) = Chunks(size);
        var data = await SendAsync(HttpMethod.Post, $"{Api}/v2/post/publish/video/init/", accessToken, JsonContent.Create(new
        {
            post_info = postInfo,
            source_info = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunkSize, total_chunk_count = count }
        }), ct);
        return (data!["publish_id"]!.GetValue<string>(), data["upload_url"]!.GetValue<string>());
    }

    /// <summary>Manda il file a pezzi, in ordine, con Content-Range. 206 = avanti, 201 = finito.</summary>
    /// <remarks>Il flusso si legge una volta sola, dall'inizio: può essere un video decifrato al volo da Bunny, che non torna indietro.</remarks>
    public async Task UploadAsync(string uploadUrl, Stream video, long size, string contentType, CancellationToken ct)
    {
        var (chunkSize, count) = Chunks(size);

        for (var i = 0; i < count; i++)
        {
            var first = i * chunkSize;
            var length = i == count - 1 ? size - first : chunkSize; // l'ultimo prende il resto
            var buffer = new byte[length];
            await video.ReadExactlyAsync(buffer, ct);

            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, size);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.PartialContent or HttpStatusCode.OK) continue;

            throw response.StatusCode == HttpStatusCode.Forbidden
                ? new SocialApiException("TikTok: l'indirizzo di caricamento è scaduto.", transient: true)
                : SocialApiException.From(response.StatusCode, "TikTok", $"caricamento del pezzo {i + 1} di {count} non riuscito");
        }
    }

    /// <param name="PostId">Solo se il post è pubblico e già approvato dalla moderazione di TikTok.</param>
    public record PublishStatus(string Status, string? FailReason, string? PostId);

    /// <summary>PROCESSING_UPLOAD, PROCESSING_DOWNLOAD, SEND_TO_USER_INBOX, PUBLISH_COMPLETE o FAILED.</summary>
    public async Task<PublishStatus> StatusAsync(string accessToken, string publishId, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, $"{Api}/v2/post/publish/status/fetch/", accessToken, JsonContent.Create(new { publish_id = publishId }), ct);
        // Il campo si chiama proprio così nella documentazione di TikTok.
        var ids = data?["publicaly_available_post_id"]?.AsArray();
        return new PublishStatus(data?["status"]?.GetValue<string>() ?? "", data?["fail_reason"]?.GetValue<string>(),
            ids is { Count: > 0 } ? ids[0]!.ToString() : null);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, string accessToken, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch (JsonException) { }
        var code = node?["error"]?["code"]?.GetValue<string>();
        if (response.IsSuccessStatusCode && code is null or "ok") return node?["data"];

        var message = node?["error"]?["message"]?.GetValue<string>();
        throw code switch
        {
            "access_token_invalid" or "scope_not_authorized" => new SocialApiException($"TikTok non accetta più l'accesso: ricollega l'account. {message}".Trim(), unauthorized: true),
            "rate_limit_exceeded" or "internal_error" => new SocialApiException($"TikTok: {message ?? "riprova più tardi"}", transient: true),
            "spam_risk_too_many_posts" => new SocialApiException("TikTok: l'account ha raggiunto il limite di post per oggi."),
            "unaudited_client_can_only_post_to_private_accounts" => new SocialApiException(
                "TikTok: finché l'app non ha passato la revisione si può pubblicare solo su account privati, con visibilità \"Solo io\"."),
            "privacy_level_option_mismatch" => new SocialApiException("TikTok: la visibilità scelta non è tra quelle permesse a questo account."),
            _ => SocialApiException.From(response.StatusCode, "TikTok", $"{message} ({code})".Trim())
        };
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}

/// <summary>Quello che si cifra per un account TikTok: i due token e quando scade quello di rinnovo.</summary>
public record TikTokSecret(string AccessToken, string RefreshToken, DateTime RefreshExpiresAtUtc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TikTokSecret From(TikTokClient.Tokens t) => new(t.AccessToken, t.RefreshToken, t.RefreshExpiresAtUtc);

    public static TikTokSecret Parse(string json) => JsonSerializer.Deserialize<TikTokSecret>(json, Json)!;

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
