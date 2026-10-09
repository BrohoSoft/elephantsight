using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// Il login di "Instagram API with Instagram Login": per gli account
/// professionali senza una Pagina Facebook. Si entra con Instagram e si
/// pubblica su <c>graph.instagram.com</c> con gli stessi passi della Graph API
/// di Facebook (container, stato, <c>media_publish</c>), che fa
/// <see cref="MetaGraphClient"/>.
/// </summary>
/// <remarks>
/// A differenza dei token di Pagina, questo scade: il login dà un token di
/// un'ora, che si scambia subito con uno da 60 giorni; il worker lo rinnova
/// prima che scada (si può dopo 24 ore di vita). Un token non rinnovato entro
/// i 60 giorni non si recupera più: va rifatto il login.
/// </remarks>
public class InstagramLoginClient(HttpClient http, IOptionsMonitor<SocialOptions> options)
{
    /// <summary>Su <see cref="Database.Entities.SocialAccount.ServerUrl"/> distingue questi account da quelli collegati tramite Facebook.</summary>
    public const string Host = "https://graph.instagram.com";

    public const string Scopes = "instagram_business_basic,instagram_business_content_publish";

    private InstagramOptions Instagram => options.CurrentValue.Instagram;

    public string AuthorizeUrl(string redirectUri, string state) =>
        $"https://www.instagram.com/oauth/authorize?client_id={E(Instagram.AppId!)}&redirect_uri={E(redirectUri)}" +
        $"&response_type=code&scope={Scopes}&state={E(state)}";

    public record Token(string AccessToken, DateTime ExpiresAtUtc);

    /// <summary>Codice del login → token di un'ora → token di 60 giorni.</summary>
    public async Task<Token> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        // Instagram aggiunge "#_" in fondo al codice nell'indirizzo di ritorno: non fa parte del codice.
        if (code.EndsWith("#_")) code = code[..^2];

        var shortLived = await SendAsync(HttpMethod.Post, "https://api.instagram.com/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Instagram.AppId!,
            ["client_secret"] = Instagram.AppSecret!,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code"] = code
        }), ct);
        // La risposta documentata è { data: [ { access_token, user_id, permissions } ] }; per prudenza si accetta anche senza "data".
        var first = shortLived?["data"]?.AsArray().FirstOrDefault() ?? shortLived;
        var token = first?["access_token"]?.GetValue<string>() ?? throw new SocialApiException("Instagram non ha restituito il token di accesso.");

        return Read(await SendAsync(HttpMethod.Get,
            $"{Host}/access_token?grant_type=ig_exchange_token&client_secret={E(Instagram.AppSecret!)}&access_token={E(token)}", null, ct));
    }

    /// <summary>Altri 60 giorni. Il token deve avere almeno 24 ore e non essere scaduto.</summary>
    public async Task<Token> RefreshAsync(string token, CancellationToken ct) =>
        Read(await SendAsync(HttpMethod.Get, $"{Host}/refresh_access_token?grant_type=ig_refresh_token&access_token={E(token)}", null, ct));

    public record Profile(string UserId, string Username, string? Name, string? AccountType);

    /// <summary>
    /// L'account. Per pubblicare si usa <c>user_id</c> (l'id dell'account
    /// professionale), non <c>id</c>, che è un id dell'utente legato all'app.
    /// </summary>
    public async Task<Profile> ProfileAsync(string token, CancellationToken ct)
    {
        var node = await SendAsync(HttpMethod.Get,
            $"{Host}/{options.CurrentValue.Meta.GraphVersion}/me?fields=user_id,username,name,account_type&access_token={E(token)}", null, ct);
        return new Profile(
            node?["user_id"]?.ToString() ?? throw new SocialApiException("Instagram non ha restituito l'id dell'account."),
            node["username"]?.GetValue<string>() ?? "",
            node["name"]?.GetValue<string>(),
            node["account_type"]?.GetValue<string>());
    }

    private static Token Read(JsonNode? node) => new(
        node?["access_token"]?.GetValue<string>() ?? throw new SocialApiException("Instagram non ha restituito il token di accesso."),
        DateTime.UtcNow.AddSeconds(node["expires_in"]?.GetValue<long>() ?? 5_184_000));

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        // Messaggi d'errore in italiano, non nella lingua della posizione del server (vedi MetaGraphClient.WithLocale).
        using var request = new HttpRequestMessage(method, MetaGraphClient.WithLocale(uri)) { Content = content };
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);

        // Due formati: { error_type, code, error_message } da api.instagram.com,
        // { error: { message, code } } da graph.instagram.com.
        JsonNode? body = null;
        try { body = JsonNode.Parse(text); } catch (JsonException) { }
        var message = body?["error_message"]?.GetValue<string>() ?? body?["error"]?["message"]?.GetValue<string>();
        var code = body?["code"]?.GetValue<int>() ?? body?["error"]?["code"]?.GetValue<int>();

        if (code == 190) throw new SocialApiException($"Instagram non accetta più l'accesso: ricollega l'account. {message}".Trim(), unauthorized: true);
        throw SocialApiException.From(response.StatusCode, "Instagram", message);
    }

    private static string E(string value) => Uri.EscapeDataString(value);
}
