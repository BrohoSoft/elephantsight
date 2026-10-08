using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Reports;

/// <summary>Un file nel bucket dei report di Google Play.</summary>
/// <param name="Md5">L'MD5 del contenuto secondo Cloud Storage: cambia solo se cambia il file.</param>
public record BucketObject(string Name, string Md5, long Size);

public enum GoogleAccessProblem
{
    /// <summary>Google rifiuta la chiave: il service account non esiste più o la chiave è stata revocata.</summary>
    InvalidKey,

    /// <summary>La chiave è buona ma non può leggere il bucket: manca il permesso in Play Console, o l'invito non è ancora attivo.</summary>
    NoBucketAccess,

    Failed
}

public class GoogleAccessException(GoogleAccessProblem problem, string message) : Exception(message)
{
    public GoogleAccessProblem Problem { get; } = problem;
}

/// <summary>
/// Il bucket <c>pubsite_prod_…</c> di Play Console, letto con l'API JSON di
/// Cloud Storage. Interfaccia per poterlo sostituire nei test.
/// </summary>
public interface IGooglePlayReports
{
    /// <summary>Si autentica una volta per sincronizzazione: il token vale mezz'ora e serve per tutte le richieste.</summary>
    Task<string> ConnectAsync(ReadOnlyMemory<byte> secret, CancellationToken ct);

    Task<IReadOnlyList<BucketObject>> ListAsync(string token, string bucket, string prefix, CancellationToken ct);

    Task<byte[]> DownloadAsync(string token, string bucket, string objectName, CancellationToken ct);
}

public class GooglePlayReports(HttpClient http) : IGooglePlayReports
{
    private const string StorageBase = "https://storage.googleapis.com/storage/v1/";
    public const string StorageScope = "https://www.googleapis.com/auth/devstorage.read_only";

    public async Task<string> ConnectAsync(ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        try
        {
            return await GoogleAuth.AccessTokenAsync(http, secret, StorageScope, ct);
        }
        catch (StoreAccessException e)
        {
            throw new GoogleAccessException(GoogleAccessProblem.InvalidKey, e.Message);
        }
    }

    public async Task<IReadOnlyList<BucketObject>> ListAsync(string token, string bucket, string prefix, CancellationToken ct)
    {
        var result = new List<BucketObject>();
        string? pageToken = null;

        do
        {
            var uri = $"{StorageBase}b/{Uri.EscapeDataString(bucket)}/o?prefix={Uri.EscapeDataString(prefix)}&fields=items(name,md5Hash,size),nextPageToken"
                + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));

            using var response = await SendAsync(token, uri, ct);
            var page = await response.Content.ReadFromJsonAsync<ObjectList>(ct);

            result.AddRange((page?.Items ?? []).Select(i => new BucketObject(i.Name, i.Md5Hash ?? "", long.TryParse(i.Size, out var s) ? s : 0)));
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    public async Task<byte[]> DownloadAsync(string token, string bucket, string objectName, CancellationToken ct)
    {
        using var response = await SendAsync(token, $"{StorageBase}b/{Uri.EscapeDataString(bucket)}/o/{Uri.EscapeDataString(objectName)}?alt=media", ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new GoogleAccessException(GoogleAccessProblem.Failed, $"Cloud Storage non risponde: {e.Message}");
        }

        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        response.Dispose();

        // 403 e 404 vogliono rimedi diversi, e il messaggio deve dire quale:
        // con il 403 il bucket c'è e manca il permesso (o l'invito non è
        // ancora attivo), con il 404 è l'indirizzo del bucket a essere sbagliato.
        throw status switch
        {
            HttpStatusCode.Forbidden => new GoogleAccessException(GoogleAccessProblem.NoBucketAccess,
                "Google risponde che il service account non ha ancora il permesso di leggere i report. Se l'hai appena invitato in Play Console è normale: l'attivazione può richiedere alcune ore, e Flarelytics riprova da solo. Altrimenti controlla in Utenti e autorizzazioni che abbia il permesso di scaricare i report in blocco."),
            HttpStatusCode.NotFound => new GoogleAccessException(GoogleAccessProblem.NoBucketAccess,
                "Google risponde che il bucket non esiste: copia di nuovo l'indirizzo da Play Console, Scarica report, Statistiche, \"Copia URI di Cloud Storage\"."),
            HttpStatusCode.Unauthorized => new GoogleAccessException(GoogleAccessProblem.InvalidKey, "Google non accetta più il token del service account."),
            _ => new GoogleAccessException(GoogleAccessProblem.Failed, $"Cloud Storage ha risposto {(int)status}.")
        };
    }

    private record ObjectList(List<ObjectItem>? Items, string? NextPageToken);
    private record ObjectItem(string Name, string? Md5Hash, string? Size);
}
