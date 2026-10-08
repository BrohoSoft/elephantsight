using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Management;

/// <summary>
/// App Store Connect API per la gestione: versioni, build, recensioni, pagina
/// dello store, caricamento delle build.
/// </summary>
/// <remarks>
/// <para>Le risposte sono JSON:API (<c>data</c>, <c>attributes</c>,
/// <c>relationships</c>, <c>included</c>, <c>links.next</c>). Si leggono come
/// <see cref="JsonNode"/> invece che con un DTO per risorsa: le risorse sono
/// decine, ne usiamo pochi campi ciascuna, e un campo in più aggiunto da Apple
/// non deve rompere niente.</para>
///
/// <para>Una sessione firma un token e lo usa per tutte le chiamate di
/// un'operazione: dura dieci minuti, più di quanto serva.</para>
/// </remarks>
public class AppleApi(HttpClient http)
{
    public AppleSession Open(StoreCredential credential, ReadOnlyMemory<byte> secret)
    {
        var key = AppleKey.Parse(credential.AppleKeyId!, credential.AppleIssuerId!, credential.AppleVendorNumber, Encoding.UTF8.GetString(secret.Span));
        return new AppleSession(http, AppStoreConnectGateway.CreateToken(key, DateTime.UtcNow));
    }
}

public class AppleSession(HttpClient http, string token)
{
    /// <summary>Tutte le pagine di una lista, seguendo <c>links.next</c>. <c>included</c> si accumula a parte.</summary>
    public async Task<(List<JsonNode> Data, List<JsonNode> Included)> ListAsync(string uri, CancellationToken ct, int maxPages = 20)
    {
        var data = new List<JsonNode>();
        var included = new List<JsonNode>();
        string? next = uri;

        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var body = await SendAsync(HttpMethod.Get, next, null, ct);
            data.AddRange(body?["data"]?.AsArray().OfType<JsonNode>() ?? []);
            included.AddRange(body?["included"]?.AsArray().OfType<JsonNode>() ?? []);
            next = body?["links"]?["next"]?.GetValue<string>();
        }

        return (data, included);
    }

    public Task<JsonNode?> GetAsync(string uri, CancellationToken ct) => SendAsync(HttpMethod.Get, uri, null, ct);

    /// <summary>Crea una risorsa: <paramref name="type"/>, attributi e relazioni (nome → (tipo, id)).</summary>
    public Task<JsonNode?> CreateAsync(string type, object attributes, IDictionary<string, (string Type, string Id)> relationships, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"v1/{type}", new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = type,
                ["attributes"] = JsonSerializer.SerializeToNode(attributes),
                ["relationships"] = Relationships(relationships)
            }
        }, ct);

    public Task<JsonNode?> UpdateAsync(string type, string id, object attributes, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"v1/{type}/{id}", new JsonObject
        {
            ["data"] = new JsonObject { ["type"] = type, ["id"] = id, ["attributes"] = JsonSerializer.SerializeToNode(attributes) }
        }, ct);

    public Task DeleteAsync(string type, string id, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"v1/{type}/{id}", null, ct);

    /// <summary>
    /// Carica un file secondo le <c>uploadOperations</c> che Apple restituisce
    /// quando si crea l'asset: ogni operazione dice URL, metodo, intestazioni e
    /// quale pezzo del file mandare. Vale per screenshot e build.
    /// </summary>
    public async Task UploadAsync(JsonNode? operations, Stream file, CancellationToken ct)
    {
        foreach (var op in operations?.AsArray().OfType<JsonNode>() ?? [])
        {
            var offset = op["offset"]!.GetValue<long>();
            var length = op["length"]!.GetValue<int>();
            var buffer = new byte[length];
            file.Seek(offset, SeekOrigin.Begin);
            await file.ReadExactlyAsync(buffer, ct);

            // Gli URL di caricamento sono di Apple ma non dell'API: niente token.
            using var request = new HttpRequestMessage(new HttpMethod(op["method"]!.GetValue<string>()), op["url"]!.GetValue<string>())
            {
                Content = new ByteArrayContent(buffer)
            };
            foreach (var header in op["requestHeaders"]?.AsArray().OfType<JsonNode>() ?? [])
            {
                var name = header["name"]!.GetValue<string>();
                var value = header["value"]!.GetValue<string>();
                if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new StoreAccessException($"Apple ha rifiutato un pezzo del file ({(int)response.StatusCode}).");
        }
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string uri, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode) throw new StoreAccessException(ErrorMessage((int)response.StatusCode, text));
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    /// <summary>Il motivo che Apple scrive in <c>errors[0].detail</c>: è quello che serve all'utente.</summary>
    private static string ErrorMessage(int status, string body)
    {
        string? detail = null;
        try { detail = JsonNode.Parse(body)?["errors"]?[0]?["detail"]?.GetValue<string>(); } catch (JsonException) { }

        return status switch
        {
            401 => "Apple non accetta più la chiave: probabilmente è stata revocata.",
            403 => $"La chiave non ha il permesso per questa operazione: serve una chiave del team con ruolo Admin o App Manager. {detail}".Trim(),
            _ => $"App Store Connect: {detail ?? $"errore {status}"}"
        };
    }

    private static JsonObject Relationships(IDictionary<string, (string Type, string Id)> relationships)
    {
        var result = new JsonObject();
        foreach (var (name, (type, id)) in relationships)
            result[name] = new JsonObject { ["data"] = new JsonObject { ["type"] = type, ["id"] = id } };
        return result;
    }
}

public static class JsonApi
{
    public static string Id(this JsonNode node) => node["id"]!.GetValue<string>();

    public static string? Attr(this JsonNode node, string name) =>
        node["attributes"]?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : node["attributes"]?[name]?.ToJsonString();

    public static T? Attr<T>(this JsonNode node, string name) =>
        node["attributes"]?[name] is JsonValue v && v.TryGetValue<T>(out var value) ? value : default;

    public static string? RelatedId(this JsonNode node, string relationship) =>
        node["relationships"]?[relationship]?["data"]?["id"]?.GetValue<string>();
}
