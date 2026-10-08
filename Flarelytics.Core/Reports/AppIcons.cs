using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Flarelytics.Core.Database.Entities;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Reports;

public record IconImage(byte[] Content, string ContentType);

/// <summary>Da dove si prendono le icone. Interfaccia per poterla sostituire nei test.</summary>
public interface IAppIconSource
{
    /// <param name="countries">
    /// Dove cercare l'app su App Store, in ordine: un'app pubblicata solo in
    /// Italia non compare nella ricerca del negozio americano.
    /// </param>
    /// <returns>L'icona, o null se lo store non ha una pagina pubblica per l'app.</returns>
    Task<IconImage?> FetchAsync(Store store, string appId, IReadOnlyList<string> countries, CancellationToken ct);
}

/// <summary>
/// Icone dalle pagine pubbliche degli store, senza le chiavi dei clienti.
/// </summary>
/// <remarks>
/// <para><b>App Store:</b> iTunes Lookup, il servizio pubblico di Apple per
/// cercare un'app dato l'Apple ID. Restituisce l'icona a 512 pixel.</para>
///
/// <para><b>Google Play:</b> l'immagine dichiarata (<c>og:image</c>) nella
/// pagina pubblica dell'app, che è l'icona. Non c'è un servizio ufficiale
/// alternativo utilizzabile: l'API che dà l'icona è la Publishing API, i cui
/// termini vietano di usarla con l'account sviluppatore di un terzo.</para>
/// </remarks>
public partial class AppIconSource(HttpClient http) : IAppIconSource
{
    private const int MaxBytes = 2 * 1024 * 1024;

    public async Task<IconImage?> FetchAsync(Store store, string appId, IReadOnlyList<string> countries, CancellationToken ct)
    {
        var url = store == Store.AppStore
            ? await AppleArtworkAsync(appId, countries, ct)
            : await GooglePlayArtworkAsync(appId, ct);

        return url is null ? null : await DownloadAsync(url, ct);
    }

    private async Task<string?> AppleArtworkAsync(string appleId, IReadOnlyList<string> countries, CancellationToken ct)
    {
        foreach (var country in countries.Append("us").Distinct())
        {
            var result = await http.GetFromJsonAsync<LookupResponse>(
                $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(appleId)}&country={country.ToLowerInvariant()}", ct);

            var app = result?.Results?.FirstOrDefault();
            if ((app?.ArtworkUrl512 ?? app?.ArtworkUrl100) is { } url) return url;
        }

        return null;
    }

    private async Task<string?> GooglePlayArtworkAsync(string packageName, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(packageName)}&hl=en", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        return GoogleIconUrl(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// L'URL dell'icona dalla pagina di Google Play, a 256 pixel. Le immagini
    /// di Google accettano la dimensione in coda all'indirizzo (<c>=s256</c>),
    /// al posto di quella che c'è.
    /// </summary>
    public static string? GoogleIconUrl(string html)
    {
        var match = OgImage().Match(html);
        if (!match.Success) return null;

        var url = WebUtility.HtmlDecode(match.Groups[1].Value);
        if (!url.StartsWith("https://", StringComparison.Ordinal)) return null;

        var sizeSuffix = url.LastIndexOf('=');
        return (sizeSuffix > url.LastIndexOf('/') ? url[..sizeSuffix] : url) + "=s256";
    }

    private async Task<IconImage?> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        // Solo immagini, e non enormi: è un file che poi serviamo noi dal
        // nostro dominio, e non deve poter diventare altro.
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type is not ("image/png" or "image/jpeg" or "image/webp")) return null;

        var content = await response.Content.ReadAsByteArrayAsync(ct);
        return content.Length is > 0 and <= MaxBytes ? new IconImage(content, type) : null;
    }

    [GeneratedRegex("""<meta[^>]+property=["']og:image["'][^>]+content=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex OgImage();

    private record LookupResponse(List<LookupApp>? Results);
    private record LookupApp(string? ArtworkUrl512, string? ArtworkUrl100);
}

/// <summary>Le icone su disco, accanto ai report.</summary>
public class IconStorage(IOptions<ReportsOptions> options)
{
    private string Root => Path.Combine(options.Value.StorageDirectory, "_icons");

    public async Task<string> WriteAsync(AppIcon icon, IconImage image, CancellationToken ct)
    {
        var extension = image.ContentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        var relative = icon.Id.ToString("N") + extension;
        Directory.CreateDirectory(Root);

        var temp = Path.Combine(Root, relative + ".tmp");
        await File.WriteAllBytesAsync(temp, image.Content, ct);
        File.Move(temp, Path.Combine(Root, relative), overwrite: true);
        return relative;
    }

    public string PathFor(string relativePath) => Path.Combine(Root, relativePath);
}
