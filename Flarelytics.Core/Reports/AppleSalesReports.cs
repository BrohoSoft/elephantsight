using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Reports;

public enum AppleFetchOutcome
{
    /// <summary>Report scaricato: <see cref="AppleFetchResult.Content"/> è il file gzip.</summary>
    Report,

    /// <summary>Apple non ha un report per quel giorno: nessuna vendita, o non ancora pronto.</summary>
    NoReport,

    /// <summary>La chiave non vale più. Inutile continuare con gli altri giorni.</summary>
    Unauthorized,

    /// <summary>La chiave è buona, ma il suo ruolo non permette di scaricare le vendite.</summary>
    Forbidden,

    /// <summary>Troppe richieste: si riprende al giro successivo.</summary>
    RateLimited,

    Failed
}

public record AppleFetchResult(AppleFetchOutcome Outcome, byte[]? Content = null, string? Message = null);

/// <summary>Sales and Trends di App Store Connect. Interfaccia per poterlo sostituire nei test.</summary>
public interface IAppleSalesReports
{
    Task<AppleFetchResult> FetchDailySalesAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, DateOnly date, CancellationToken ct);

    /// <summary>SKU e Apple ID di tutte le app dell'account.</summary>
    Task<IReadOnlyList<(string Sku, string AppleId)>> ListAppSkusAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct);
}

public class AppleSalesReports(HttpClient http) : IAppleSalesReports
{
    /// <summary>
    /// SALES / SUMMARY / DAILY esiste solo in versione 1_0 attraverso l'API.
    /// Il parser legge le colonne per nome, quindi una versione nuova con
    /// colonne in più non lo romperebbe.
    /// </summary>
    private const string Version = "1_0";

    public async Task<AppleFetchResult> FetchDailySalesAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, DateOnly date, CancellationToken ct)
    {
        if (credential.AppleVendorNumber is null)
        {
            return new(AppleFetchOutcome.Failed, Message: "Manca il Vendor Number: aggiungilo alla chiave per scaricare le vendite.");
        }

        var uri = "v1/salesReports"
            + "?filter[frequency]=DAILY&filter[reportType]=SALES&filter[reportSubType]=SUMMARY"
            + $"&filter[version]={Version}&filter[vendorNumber]={Uri.EscapeDataString(credential.AppleVendorNumber)}"
            + $"&filter[reportDate]={date:yyyy-MM-dd}";

        using var request = Request(credential, secret, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/a-gzip"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            return new(AppleFetchOutcome.Failed, Message: $"App Store Connect non risponde: {e.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return new(AppleFetchOutcome.Report, await response.Content.ReadAsByteArrayAsync(ct));
            }

            // Apple spiega il motivo nel corpo, in errors[0].detail: è quello
            // che serve all'utente per capire cosa sistemare.
            var detail = await AppleErrorDetailAsync(response, ct);

            return response.StatusCode switch
            {
                // Apple risponde 404 sia per "nessuna vendita quel giorno" sia
                // per "report non ancora pronto". Chi chiama distingue dalla data.
                HttpStatusCode.NotFound => new(AppleFetchOutcome.NoReport),

                HttpStatusCode.Unauthorized => new(AppleFetchOutcome.Unauthorized,
                    Message: $"Apple non accetta più la chiave: probabilmente è stata revocata. ({detail})"),

                HttpStatusCode.Forbidden => new(AppleFetchOutcome.Forbidden,
                    Message: $"La chiave non può scaricare i report di vendita: serve una chiave del team con accesso Finance o Sales, e il Vendor Number giusto. Apple dice: {detail}"),

                HttpStatusCode.TooManyRequests => new(AppleFetchOutcome.RateLimited, Message: "Apple ha chiesto di rallentare."),

                _ => new(AppleFetchOutcome.Failed, Message: $"App Store Connect ha risposto {(int)response.StatusCode}: {detail}")
            };
        }
    }

    public async Task<IReadOnlyList<(string Sku, string AppleId)>> ListAppSkusAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        string? next = "v1/apps?limit=200&fields[apps]=sku";

        while (next is not null)
        {
            using var request = Request(credential, secret, next);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new StoreAccessException($"App Store Connect ha risposto {(int)response.StatusCode} alla richiesta delle app.");

            var page = await response.Content.ReadFromJsonAsync<AppsPage>(ct);
            result.AddRange((page?.Data ?? []).Where(a => a.Attributes.Sku is not null).Select(a => (a.Attributes.Sku!, a.Id)));
            next = page?.Links?.Next;
        }

        return result;
    }

    private static async Task<string> AppleErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            var error = body?.Errors?.FirstOrDefault();
            return error is null ? $"HTTP {(int)response.StatusCode}" : $"{error.Title} – {error.Detail}";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    private static HttpRequestMessage Request(StoreCredential credential, ReadOnlyMemory<byte> secret, string uri)
    {
        var key = AppleKey.Parse(credential.AppleKeyId!, credential.AppleIssuerId!, credential.AppleVendorNumber, Encoding.UTF8.GetString(secret.Span));
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppStoreConnectGateway.CreateToken(key, DateTime.UtcNow));
        return request;
    }

    private record ErrorBody(List<AppleError>? Errors);
    private record AppleError(string? Code, string? Title, string? Detail);
    private record AppsPage(List<AppResource> Data, PageLinks? Links);
    private record AppResource(string Id, AppAttributes Attributes);
    private record AppAttributes(string? Sku);
    private record PageLinks([property: JsonPropertyName("next")] string? Next);
}
