using System.Security.Cryptography;
using System.Text;

namespace Flarelytics.Core.Secrets;

/// <summary>
/// Cifra i piccoli segreti che stanno in una colonna del database, come il
/// seme del TOTP.
/// </summary>
/// <remarks>
/// <para>Usa le stesse chiavi master di <see cref="SecretVault"/>: chi ruba il
/// database senza la cartella delle chiavi ha in mano testo cifrato. Un seme
/// TOTP in chiaro, insieme all'hash della password, renderebbe la seconda
/// verifica inutile proprio nel caso per cui esiste.</para>
///
/// <para>Il <c>context</c> entra come dato associato: un valore copiato sulla
/// riga di un altro utente non si decifra. Formato:
/// <c>&lt;versione chiave&gt;:&lt;base64(nonce | tag | dati cifrati)&gt;</c>.</para>
/// </remarks>
public sealed class FieldProtector(KeyRing keys)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(ReadOnlySpan<byte> plaintext, string context)
    {
        var version = keys.ActiveVersion;
        var payload = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = payload.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(keys.Get(version), TagSize);
        aes.Encrypt(nonce, plaintext, payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), Aad(version, context));

        return version + ":" + Convert.ToBase64String(payload);
    }

    /// <remarks>Il chiamante azzera l'array restituito quando ha finito.</remarks>
    public byte[] Unprotect(string protectedValue, string context)
    {
        var separator = protectedValue.IndexOf(':');
        if (separator <= 0) throw new CryptographicException("Valore protetto malformato.");

        var version = protectedValue[..separator];
        var payload = Convert.FromBase64String(protectedValue[(separator + 1)..]);
        var plaintext = new byte[payload.Length - NonceSize - TagSize];

        using var aes = new AesGcm(keys.Get(version), TagSize);
        aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), plaintext, Aad(version, context));

        return plaintext;
    }

    private static byte[] Aad(string version, string context) => Encoding.UTF8.GetBytes($"field|{version}|{context}");
}
