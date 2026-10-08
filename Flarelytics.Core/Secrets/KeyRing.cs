using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Secrets;

/// <summary>
/// Le chiavi master (KEK), caricate una volta all'avvio.
/// </summary>
/// <remarks>
/// <para>Più versioni convivono per poter ruotare: si aggiunge un file nuovo, si
/// sposta <see cref="SecretsOptions.ActiveKeyVersion"/>, si ricifrano le chiavi
/// dei file con <see cref="SecretVault.RewrapAsync"/> e solo alla fine si toglie
/// il file vecchio.</para>
///
/// <para>Il costruttore fallisce se manca la versione attiva o se una chiave
/// non è lunga 32 byte: meglio un'applicazione che non parte che una che
/// accetta credenziali e non riesce a cifrarle.</para>
/// </remarks>
public sealed class KeyRing
{
    public const int KeySize = 32;

    private readonly Dictionary<string, byte[]> _keys;

    public string ActiveVersion { get; }

    public KeyRing(IOptions<SecretsOptions> options)
    {
        var o = options.Value;

        if (string.IsNullOrWhiteSpace(o.KeysDirectory) || !Directory.Exists(o.KeysDirectory))
        {
            throw new InvalidOperationException($"La cartella delle chiavi master non esiste: '{o.KeysDirectory}'.");
        }

        _keys = Directory.EnumerateFiles(o.KeysDirectory, "*.key")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, LoadKey, StringComparer.Ordinal);

        if (!_keys.ContainsKey(o.ActiveKeyVersion))
        {
            throw new InvalidOperationException(
                $"Manca la chiave master attiva '{o.ActiveKeyVersion}.key' in '{o.KeysDirectory}'.");
        }

        ActiveVersion = o.ActiveKeyVersion;
    }

    public byte[] Get(string version) =>
        _keys.TryGetValue(version, out var key)
            ? key
            : throw new CryptographicException($"La chiave master '{version}' non è disponibile.");

    private static byte[] LoadKey(string path)
    {
        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());

        return key.Length == KeySize
            ? key
            : throw new InvalidOperationException($"La chiave master '{path}' non è lunga {KeySize} byte.");
    }

    /// <summary>
    /// Scrive una chiave nuova. Serve solo in sviluppo e nei test: in
    /// produzione la chiave si genera a mano, una volta, e si custodisce fuori
    /// dal server.
    /// </summary>
    public static void CreateKeyFile(string directory, string version)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, version + ".key");

        if (File.Exists(path)) return;

        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
