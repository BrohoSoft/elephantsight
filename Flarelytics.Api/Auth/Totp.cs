using System.Security.Cryptography;
using System.Text;

namespace Flarelytics.Api.Auth;

/// <summary>
/// Codici a tempo (RFC 6238): quelli di Google Authenticator, 1Password, Authy.
/// </summary>
/// <remarks>
/// HMAC-SHA1, 6 cifre, 30 secondi: non sono le scelte più moderne, ma sono le
/// sole che tutte le app supportano. Sono anche quelle che le app assumono
/// quando l'URI non dice niente.
/// </remarks>
public static class Totp
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>160 bit, la lunghezza che la RFC raccomanda per SHA-1.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTime nowUtc) => new DateTimeOffset(nowUtc).ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    public static string Code(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Verifica un codice accettando l'intervallo precedente e quello
    /// successivo: l'orologio del telefono non è mai preciso al secondo.
    /// </summary>
    /// <returns>L'intervallo a cui il codice corrisponde, o null.</returns>
    /// <param name="lastUsedStep">Gli intervalli fino a questo compreso sono già stati usati e non valgono più.</param>
    public static long? Verify(ReadOnlySpan<byte> secret, string code, DateTime nowUtc, long? lastUsedStep)
    {
        code = code.Replace(" ", "");
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;

        var current = StepAt(nowUtc);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep is { } last && step <= last) continue;

            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>L'URI da mettere nel QR code.</summary>
    public static string Uri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={(int)Step.TotalSeconds}";

    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
}
