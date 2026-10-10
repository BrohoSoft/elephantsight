using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social;

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

    /// <summary>Il suffisso del nome di una miniatura: <c>{tenant}{media}.thumb.jpg</c>.</summary>
    public const string ThumbnailSuffix = ".thumb.jpg";

    /// <summary>Il percorso relativo, da prefissare con l'indirizzo pubblico quando serve a una rete esterna.</summary>
    public string PathFor(Database.Entities.SocialMedia media, DateTime expiresAtUtc)
    {
        var id = media.TenantId.ToString("N") + media.Id.ToString("N");
        var expires = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return $"{RoutePrefix}{id}{media.Extension}?e={expires}&s={Signature(id, expires)}";
    }

    /// <summary>
    /// La miniatura: un indirizzo a parte, con una firma sua (la firma di una
    /// miniatura non apre l'originale, e viceversa).
    /// </summary>
    public string ThumbnailPathFor(Database.Entities.SocialMedia media, DateTime expiresAtUtc)
    {
        var id = media.TenantId.ToString("N") + media.Id.ToString("N");
        var expires = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        return $"{RoutePrefix}{id}{ThumbnailSuffix}?e={expires}&s={Signature(id + "|thumb", expires)}";
    }

    /// <summary>Controlla firma e scadenza; se vanno bene restituisce tenant e immagine.</summary>
    /// <param name="thumbnail">L'indirizzo è quello di una miniatura (firmata a parte).</param>
    public bool TryVerify(string id, long expires, string? signature, DateTime nowUtc, out Guid tenantId, out Guid mediaId, bool thumbnail = false)
    {
        tenantId = mediaId = Guid.Empty;
        if (id.Length != 64 || signature is null) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime < nowUtc) return false;

        var expected = Encoding.ASCII.GetBytes(Signature(thumbnail ? id + "|thumb" : id, expires));
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
