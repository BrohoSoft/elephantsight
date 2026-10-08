using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// Un App Store Connect e un Google Play finti, a livello HTTP: si registrano
/// le risposte per metodo e percorso, e si leggono le richieste ricevute.
/// </summary>
/// <remarks>
/// Si attacca ai client veri (<c>AppleApi</c>, <c>GooglePublisher</c>) come
/// gestore HTTP, quindi i test passano dallo stesso codice della produzione e
/// vedono esattamente cosa mandiamo agli store.
/// </remarks>
public class FakeStoreServer : HttpMessageHandler
{
    public record Recorded(HttpMethod Method, string Url, string? Body, byte[]? Bytes);

    private readonly List<(HttpMethod Method, Regex Path, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
    public ConcurrentQueue<Recorded> Requests { get; } = new();

    public FakeStoreServer()
    {
        // Il token di Google: sempre buono.
        On(HttpMethod.Post, "^https://oauth2.googleapis.com/token$", """{"access_token":"token-google","expires_in":3600}""");
    }

    public FakeStoreServer On(HttpMethod method, string pathPattern, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Insert(0, (method, new Regex(pathPattern), _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        return this;
    }

    public IEnumerable<Recorded> Calls(HttpMethod method, string contains) =>
        Requests.Where(r => r.Method == method && r.Url.Contains(contains));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        var url = request.RequestUri!.ToString();
        var text = bytes is null ? null : Encoding.UTF8.GetString(bytes);
        Requests.Enqueue(new Recorded(request.Method, url, text, bytes));

        // Si confronta l'URL senza la query, che resta comunque registrata.
        var path = url.Split('?')[0];
        foreach (var (method, pattern, respond) in _routes)
        {
            if (method == request.Method && pattern.IsMatch(path)) return respond(request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($$"""{"errors":[{"detail":"Nessuna risposta finta per {{request.Method}} {{path}}"}]}""")
        };
    }
}
