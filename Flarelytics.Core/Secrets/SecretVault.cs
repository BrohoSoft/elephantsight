using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Secrets;

/// <summary>
/// Cifra su disco i segreti caricati dai tenant (i .p8 di Apple, i JSON dei
/// service account Google) e li rilegge solo quando servono.
/// </summary>
/// <remarks>
/// <para><b>Cifratura a busta.</b> Ogni file ha la sua chiave dati (DEK),
/// casuale, che cifra il contenuto con AES-256-GCM. La DEK a sua volta è
/// cifrata con la chiave master (KEK) e salvata nell'intestazione del file. La
/// KEK non tocca mai i dati: ruotarla vuol dire ricifrare 32 byte per file, non
/// il file intero.</para>
///
/// <para><b>Il file è legato al suo posto.</b> Tenant e credenziale entrano
/// come dati associati in entrambe le cifrature: un file copiato sotto un altro
/// tenant, o scambiato con quello di un'altra credenziale, non si decifra.
/// Senza questo legame chi può scrivere sul disco potrebbe far usare la propria
/// chiave al posto di quella di un cliente.</para>
///
/// <para>Formato, in ordine:</para>
/// <code>
/// "FLKV"  | formato (1) | lunghezza versione KEK (1) | versione KEK (utf-8)
/// nonce DEK (12) | DEK cifrata (32) | tag DEK (16)
/// nonce dati (12) | tag dati (16) | dati cifrati (resto del file)
/// </code>
/// </remarks>
public sealed class SecretVault(KeyRing keys, IOptions<SecretsOptions> options)
{
    private static readonly byte[] Magic = "FLKV"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _root = options.Value.StorageDirectory;

    /// <summary>
    /// Cifra e scrive il segreto, sostituendo quello che c'era.
    /// </summary>
    /// <returns>La versione della chiave master usata, da salvare sulla credenziale.</returns>
    public async Task<string> WriteAsync(Guid tenantId, Guid credentialId, ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        var dek = RandomNumberGenerator.GetBytes(KeyRing.KeySize);
        try
        {
            var version = keys.ActiveVersion;
            var header = WrapKey(dek, version, tenantId, credentialId);

            var dataNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var dataTag = new byte[TagSize];
            var ciphertext = new byte[plaintext.Length];

            using (var aes = new AesGcm(dek, TagSize))
            {
                aes.Encrypt(dataNonce, plaintext.Span, ciphertext, dataTag, DataAad(tenantId, credentialId));
            }

            await WriteAtomicallyAsync(PathFor(tenantId, credentialId), [header, dataNonce, dataTag, ciphertext], ct);
            return version;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Rilegge e decifra il segreto.
    /// </summary>
    /// <remarks>
    /// Il chiamante possiede l'array e lo deve azzerare con
    /// <see cref="CryptographicOperations.ZeroMemory"/> appena ha finito: è
    /// l'unico posto in cui il segreto esiste in chiaro.
    /// </remarks>
    public async Task<byte[]> ReadAsync(Guid tenantId, Guid credentialId, CancellationToken ct)
    {
        var file = await File.ReadAllBytesAsync(PathFor(tenantId, credentialId), ct);
        var (_, dek, offset) = UnwrapKey(file, tenantId, credentialId);

        try
        {
            var dataNonce = file.AsSpan(offset, NonceSize);
            var dataTag = file.AsSpan(offset + NonceSize, TagSize);
            var ciphertext = file.AsSpan(offset + NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(dataNonce, ciphertext, dataTag, plaintext, DataAad(tenantId, credentialId));

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Ricifra la chiave dati del file con la chiave master attiva, senza
    /// toccare i dati. È il passo di una rotazione.
    /// </summary>
    /// <returns>La versione della chiave master ora in uso sul file.</returns>
    public async Task<string> RewrapAsync(Guid tenantId, Guid credentialId, CancellationToken ct)
    {
        var path = PathFor(tenantId, credentialId);
        var file = await File.ReadAllBytesAsync(path, ct);
        var (version, dek, offset) = UnwrapKey(file, tenantId, credentialId);

        try
        {
            if (version == keys.ActiveVersion) return version;

            var header = WrapKey(dek, keys.ActiveVersion, tenantId, credentialId);
            await WriteAtomicallyAsync(path, [header, file[offset..]], ct);
            return keys.ActiveVersion;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public void Delete(Guid tenantId, Guid credentialId)
    {
        var path = PathFor(tenantId, credentialId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Cancella tutti i segreti di un tenant: con il file se ne va l'unica copia della sua DEK.</summary>
    public void DeleteTenant(Guid tenantId)
    {
        var directory = Path.Combine(_root, tenantId.ToString("N"));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public string PathFor(Guid tenantId, Guid credentialId) =>
        Path.Combine(_root, tenantId.ToString("N"), credentialId.ToString("N") + ".bin");

    private byte[] WrapKey(byte[] dek, string version, Guid tenantId, Guid credentialId)
    {
        var versionBytes = Encoding.UTF8.GetBytes(version);
        var header = new byte[Magic.Length + 2 + versionBytes.Length + NonceSize + KeyRing.KeySize + TagSize];
        var span = header.AsSpan();

        Magic.CopyTo(span);
        span[Magic.Length] = FormatVersion;
        span[Magic.Length + 1] = checked((byte)versionBytes.Length);
        versionBytes.CopyTo(span[(Magic.Length + 2)..]);

        var rest = span[(Magic.Length + 2 + versionBytes.Length)..];
        var nonce = rest[..NonceSize];
        var wrapped = rest.Slice(NonceSize, KeyRing.KeySize);
        var tag = rest.Slice(NonceSize + KeyRing.KeySize, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys.Get(version), TagSize);
        aes.Encrypt(nonce, dek, wrapped, tag, KeyAad(version, tenantId, credentialId));

        return header;
    }

    private (string Version, byte[] Dek, int Offset) UnwrapKey(byte[] file, Guid tenantId, Guid credentialId)
    {
        var span = file.AsSpan();

        if (span.Length < Magic.Length + 2 || !span[..Magic.Length].SequenceEqual(Magic) || span[Magic.Length] != FormatVersion)
        {
            throw new CryptographicException("Il file non è un segreto cifrato da WatchStore o ha un formato sconosciuto.");
        }

        var versionLength = span[Magic.Length + 1];
        var version = Encoding.UTF8.GetString(span.Slice(Magic.Length + 2, versionLength));
        var offset = Magic.Length + 2 + versionLength;

        var nonce = span.Slice(offset, NonceSize);
        var wrapped = span.Slice(offset + NonceSize, KeyRing.KeySize);
        var tag = span.Slice(offset + NonceSize + KeyRing.KeySize, TagSize);
        var dek = new byte[KeyRing.KeySize];

        using var aes = new AesGcm(keys.Get(version), TagSize);
        aes.Decrypt(nonce, wrapped, tag, dek, KeyAad(version, tenantId, credentialId));

        return (version, dek, offset + NonceSize + KeyRing.KeySize + TagSize);
    }

    private static byte[] DataAad(Guid tenantId, Guid credentialId) =>
        Encoding.UTF8.GetBytes($"data|{tenantId:N}|{credentialId:N}");

    private static byte[] KeyAad(string version, Guid tenantId, Guid credentialId) =>
        Encoding.UTF8.GetBytes($"key|{version}|{tenantId:N}|{credentialId:N}");

    /// <summary>
    /// Scrive in un file temporaneo nella stessa cartella e poi lo rinomina:
    /// la rinomina è atomica, quindi chi legge trova il file vecchio o quello
    /// nuovo, mai uno scritto a metà.
    /// </summary>
    private static async Task WriteAtomicallyAsync(string path, byte[][] parts, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        CreatePrivateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            await using (var stream = new FileStream(temp, streamOptions))
            {
                foreach (var part in parts) await stream.WriteAsync(part, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
