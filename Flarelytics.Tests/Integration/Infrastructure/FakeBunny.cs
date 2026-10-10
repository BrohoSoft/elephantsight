using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// Una storage zone di Bunny finta, in memoria, attaccata al
/// <c>BunnyStorageClient</c> vero: PUT, GET (anche con Range) e DELETE come
/// l'API di Bunny, con la password della zone nell'header <c>AccessKey</c>.
/// Tiene i file così come arrivano, per controllare che siano cifrati.
/// </summary>
public class FakeBunny : HttpMessageHandler
{
    public const string Zone = "zona-test";
    public const string Password = "password-della-zone-segreta";

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentQueue<(HttpMethod Method, string Path, string? Range)> Requests { get; } = new();

    /// <summary>Come fa Bunny quando non onora l'header: risponde 200 con il file intero.</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>Tutte le richieste falliscono come se Bunny non rispondesse.</summary>
    public bool Down { get; set; }

    public static Dictionary<string, string?> Settings(string region = "") => new()
    {
        ["Media:Bunny:StorageZone"] = Zone,
        ["Media:Bunny:Region"] = region,
        ["Media:Bunny:AccessKey"] = Password,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var prefix = $"/{Zone}/";
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        Requests.Enqueue((request.Method, path, request.Headers.Range?.ToString()));
        if (Down) throw new HttpRequestException("Connessione rifiutata");

        if (!uri.Host.EndsWith("storage.bunnycdn.com") || !path.StartsWith(prefix)
            || !request.Headers.TryGetValues("AccessKey", out var keys) || keys.Single() != Password)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"HttpCode":401,"Message":"Unauthorized"}""") };

        var key = path[prefix.Length..];
        if (request.Method == HttpMethod.Put)
        {
            Files[key] = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
        if (request.Method == HttpMethod.Delete)
            return new HttpResponseMessage(Files.TryRemove(key, out _) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        if (request.Method != HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

        if (!Files.TryGetValue(key, out var file)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Headers.Range is { } range && !IgnoreRange)
        {
            var r = range.Ranges.Single();
            var from = r.From ?? file.Length - r.To!.Value;
            var to = r.From is null ? file.Length - 1 : Math.Min(r.To ?? file.Length - 1, file.Length - 1);
            var part = new ByteArrayContent(file, (int)from, (int)(to - from + 1));
            part.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = part };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
    }
}
