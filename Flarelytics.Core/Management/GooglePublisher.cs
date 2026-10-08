using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Management;

/// <summary>
/// Google Play Android Developer API (la "Publishing API"): canali e release,
/// bundle, pagina dello store, recensioni.
/// </summary>
/// <remarks>
/// <para>Quasi tutto passa da un <b>edit</b>: si apre, si legge o si modifica,
/// e alla fine lo si conferma (<c>commit</c>) o lo si butta. Un edit aperto e
/// non confermato non cambia niente sullo store, quindi le letture lo
/// cancellano sempre alla fine.</para>
///
/// <para>Uso consentito perché Flarelytics è self-hosted: chi lo installa lo
/// usa per le proprie app, con il proprio account.</para>
/// </remarks>
public class GooglePublisher(HttpClient http)
{
    public const string Scope = "https://www.googleapis.com/auth/androidpublisher";
    private const string Base = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";
    private const string UploadBase = "https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications/";

    public async Task<GoogleSession> OpenAsync(ReadOnlyMemory<byte> secret, CancellationToken ct) =>
        new(this, await GoogleAuth.AccessTokenAsync(http, secret, Scope, ct));

    internal async Task<JsonNode?> SendAsync(string token, HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new StoreAccessException(ErrorMessage((int)response.StatusCode, text));

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    internal static string App(string packageName) => Base + Uri.EscapeDataString(packageName);
    internal static string UploadApp(string packageName) => UploadBase + Uri.EscapeDataString(packageName);

    private static string ErrorMessage(int status, string body)
    {
        string? message = null;
        try { message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch (JsonException) { }

        return status switch
        {
            401 => "Google non accetta più la chiave del service account.",
            403 => $"Il service account non ha il permesso per questa operazione: in Play Console, Utenti e autorizzazioni, servono i permessi di rilascio e di gestione della scheda, e la Google Play Android Developer API abilitata nel progetto Cloud. {message}".Trim(),
            404 => $"Google Play non trova l'app: controlla il package name e che il service account la veda. {message}".Trim(),
            _ => $"Google Play: {message ?? $"errore {status}"}"
        };
    }
}

public class GoogleSession(GooglePublisher publisher, string token)
{
    public Task<JsonNode?> GetAsync(string uri, CancellationToken ct) => publisher.SendAsync(token, HttpMethod.Get, uri, null, ct);

    public Task<JsonNode?> SendJsonAsync(HttpMethod method, string uri, object body, CancellationToken ct) =>
        publisher.SendAsync(token, method, uri, JsonContent.Create(body), ct);

    public Task<JsonNode?> UploadAsync(string uri, Stream content, string contentType, CancellationToken ct)
    {
        var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return publisher.SendAsync(token, HttpMethod.Post, uri, body, ct);
    }

    public Task DeleteAsync(string uri, CancellationToken ct) => publisher.SendAsync(token, HttpMethod.Delete, uri, null, ct);

    /// <summary>
    /// Apre un edit, esegue <paramref name="work"/> e poi lo conferma (se
    /// <paramref name="commit"/>) o lo cancella. Se qualcosa va storto a metà
    /// l'edit si cancella comunque: sullo store non resta niente di parziale.
    /// </summary>
    public async Task<T> WithEditAsync<T>(string packageName, bool commit, Func<string, Task<T>> work, CancellationToken ct)
    {
        var app = GooglePublisher.App(packageName);
        var edit = await publisher.SendAsync(token, HttpMethod.Post, $"{app}/edits", JsonContent.Create(new { }), ct);
        var editId = edit!["id"]!.GetValue<string>();

        try
        {
            var result = await work(editId);
            if (commit) await publisher.SendAsync(token, HttpMethod.Post, $"{app}/edits/{editId}:commit", null, ct);
            else await TryDeleteAsync(app, editId);
            return result;
        }
        catch
        {
            await TryDeleteAsync(app, editId);
            throw;
        }
    }

    private async Task TryDeleteAsync(string app, string editId)
    {
        try { await publisher.SendAsync(token, HttpMethod.Delete, $"{app}/edits/{editId}", null, CancellationToken.None); }
        catch (Exception e) when (e is StoreAccessException or HttpRequestException) { /* l'edit scade da solo */ }
    }
}
