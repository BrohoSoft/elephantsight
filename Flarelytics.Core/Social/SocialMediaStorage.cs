using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

/// <summary>
/// Le immagini e i video dei post, su disco accanto ai report:
/// <c>{Reports}/_social/{tenant}/{id}.jpg|.mp4|.mov</c>. I video in arrivo a
/// pezzi stanno in <c>{id}.part</c> finché non sono completi.
/// </summary>
public class SocialMediaStorage(IOptions<ReportsOptions> options)
{
    public string Root => Path.Combine(options.Value.StorageDirectory, "_social");

    public string PathFor(Guid tenantId, Guid mediaId, string extension) =>
        Path.Combine(Root, tenantId.ToString("N"), mediaId.ToString("N") + extension);

    public string PathFor(Database.Entities.SocialMedia media) => PathFor(media.TenantId, media.Id, media.Extension);

    public async Task WriteAsync(Database.Entities.SocialMedia media, byte[] content, CancellationToken ct)
    {
        var path = PathFor(media);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }

    public Task<byte[]> ReadAsync(Database.Entities.SocialMedia media, CancellationToken ct) => File.ReadAllBytesAsync(PathFor(media), ct);

    public FileStream OpenRead(Database.Entities.SocialMedia media) => File.OpenRead(PathFor(media));

    /// <summary>Il file di <paramref name="source"/> anche per <paramref name="copy"/>. Falso se l'originale non c'è più.</summary>
    public bool Copy(Database.Entities.SocialMedia source, Database.Entities.SocialMedia copy)
    {
        var from = PathFor(source);
        if (!File.Exists(from)) return false;
        File.Copy(from, PathFor(copy), overwrite: true);
        return true;
    }

    public void Delete(Database.Entities.SocialMedia media)
    {
        var path = PathFor(media);
        if (File.Exists(path)) File.Delete(path);
    }

    // --- caricamento a pezzi ---

    public string PartPath(Guid tenantId, Guid uploadId)
    {
        var path = PathFor(tenantId, uploadId, ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Il file completo prende il suo nome definitivo: l'id del caricamento diventa l'id dell'immagine o del video.</summary>
    public void Promote(Guid tenantId, Guid uploadId, string extension) =>
        File.Move(PartPath(tenantId, uploadId), PathFor(tenantId, uploadId, extension), overwrite: true);

    public void DeletePart(Guid tenantId, Guid uploadId)
    {
        var path = PathFor(tenantId, uploadId, ".part");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>I pezzi abbandonati: caricamenti mai completati da più di un giorno.</summary>
    public int DeleteStaleParts(TimeSpan olderThan)
    {
        if (!Directory.Exists(Root)) return 0;
        var stale = Directory.EnumerateFiles(Root, "*.part", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow - olderThan).ToList();
        foreach (var f in stale) File.Delete(f);
        return stale.Count;
    }
}

/// <summary>
/// Gli indirizzi delle immagini, firmati e a scadenza.
/// </summary>
/// <remarks>
/// <para>Instagram non accetta un caricamento: vuole un URL pubblico da cui
/// scaricare l'immagine. E nel pannello un <c>&lt;img&gt;</c> non può mandare
/// l'access token. Per tutti e due si usa un indirizzo che contiene tenant e
/// immagine, una scadenza e un HMAC: chi lo serve non ha bisogno del database
/// (né quindi del tenant), e un indirizzo vale solo per quell'immagine e per
/// poco tempo.</para>
///
/// <para>La chiave dell'HMAC deriva dalla chiave master (HKDF), così non c'è
/// un altro segreto da custodire.</para>
/// </remarks>
public class MediaUrlSigner(KeyRing keys)
{
    public const string RoutePrefix = "/api/v1/social/media/";

    /// <summary>Il percorso relativo, da prefissare con l'indirizzo pubblico quando serve a una rete esterna.</summary>
    public string PathFor(Database.Entities.SocialMedia media, DateTime expiresAtUtc)
    {
        var id = media.TenantId.ToString("N") + media.Id.ToString("N");
        var expires = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return $"{RoutePrefix}{id}{media.Extension}?e={expires}&s={Signature(id, expires)}";
    }

    /// <summary>Controlla firma e scadenza; se vanno bene restituisce tenant e immagine.</summary>
    public bool TryVerify(string id, long expires, string? signature, DateTime nowUtc, out Guid tenantId, out Guid mediaId)
    {
        tenantId = mediaId = Guid.Empty;
        if (id.Length != 64 || signature is null) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime < nowUtc) return false;

        var expected = Encoding.ASCII.GetBytes(Signature(id, expires));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature))) return false;

        return Guid.TryParseExact(id[..32], "N", out tenantId) && Guid.TryParseExact(id[32..], "N", out mediaId);
    }

    private string Signature(string id, long expires)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, keys.Get(keys.ActiveVersion), 32, info: "watchstore-social-media-url"u8.ToArray());
        try
        {
            return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{id}|{expires}")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>Le dimensioni di un JPEG, lette dall'intestazione: servono per le proporzioni (Instagram) e per Bluesky.</summary>
public static class JpegInfo
{
    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

        var i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return false;
            var marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; } // riempimento
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { i += 2; continue; } // marcatori senza lunghezza

            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2) return false;

            // SOF0…SOF15, tranne DHT (C4), JPG (C8) e DAC (CC), che non sono frame.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 > data.Length) return false;
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }
}
