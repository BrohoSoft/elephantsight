using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flarelytics.Core.Stores;

/// <summary>Un file caricato che non è quello che dice di essere. Il messaggio va mostrato all'utente.</summary>
public class InvalidStoreKeyException(string message) : Exception(message);

/// <summary>
/// La chiave .p8 di App Store Connect, letta e controllata.
/// </summary>
/// <remarks>
/// Apple la dà come PKCS#8 in PEM: una chiave EC sulla curva P-256, che firma i
/// JWT in ES256. Una chiave di un altro tipo verrebbe accettata dal parser ma
/// rifiutata da Apple con un 401 che non spiega niente: si scarta qui.
/// </remarks>
public sealed partial record AppleKey(string KeyId, string IssuerId, string? VendorNumber, string PrivateKeyPem)
{
    public static AppleKey Parse(string keyId, string issuerId, string? vendorNumber, string privateKeyPem)
    {
        keyId = keyId.Trim();
        issuerId = issuerId.Trim().ToLowerInvariant();
        vendorNumber = string.IsNullOrWhiteSpace(vendorNumber) ? null : vendorNumber.Trim();

        if (!KeyIdPattern().IsMatch(keyId))
            throw new InvalidStoreKeyException("Il Key ID è di 10 caratteri, lettere maiuscole e cifre: lo trovi accanto alla chiave in App Store Connect.");

        if (!Guid.TryParse(issuerId, out _))
            throw new InvalidStoreKeyException("L'Issuer ID è un UUID: lo trovi in cima alla pagina delle chiavi in App Store Connect.");

        if (vendorNumber is not null && !vendorNumber.All(char.IsAsciiDigit))
            throw new InvalidStoreKeyException("Il Vendor Number contiene solo cifre: lo trovi in Pagamenti e resoconti finanziari.");

        var key = new AppleKey(keyId, issuerId, vendorNumber, privateKeyPem.Trim());
        using var ecdsa = key.CreateSigner();
        return key;
    }

    /// <summary>La chiave pronta per firmare. Va eliminata dopo l'uso.</summary>
    public ECDsa CreateSigner()
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(PrivateKeyPem);
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new InvalidStoreKeyException("Il file non è una chiave .p8 valida: caricalo così come l'hai scaricato da App Store Connect.");
        }

        if (ecdsa.KeySize != 256)
        {
            ecdsa.Dispose();
            throw new InvalidStoreKeyException("La chiave non è sulla curva P-256: non è una chiave di App Store Connect.");
        }

        return ecdsa;
    }

    public string Fingerprint()
    {
        using var ecdsa = CreateSigner();
        return Convert.ToHexString(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    [GeneratedRegex("^[A-Z0-9]{10}$")]
    private static partial Regex KeyIdPattern();
}

/// <summary>
/// Il JSON di un service account Google, letto e controllato.
/// </summary>
/// <remarks>
/// È il file che Google Cloud fa scaricare alla creazione della chiave: dentro
/// c'è la chiave privata RSA con cui ci si presenta a Google per ottenere un
/// token. L'unica parte che si salva fuori dal file cifrato è
/// <see cref="ClientEmail"/>, che non è un segreto: è l'indirizzo che il
/// cliente invita in Play Console.
/// </remarks>
public sealed record GoogleServiceAccount(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("private_key_id")] string? PrivateKeyId,
    [property: JsonPropertyName("private_key")] string? PrivateKey,
    [property: JsonPropertyName("client_email")] string? ClientEmail,
    [property: JsonPropertyName("token_uri")] string? TokenUri)
{
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    public static GoogleServiceAccount Parse(string json)
    {
        GoogleServiceAccount? account;
        try
        {
            account = JsonSerializer.Deserialize<GoogleServiceAccount>(json);
        }
        catch (JsonException)
        {
            throw new InvalidStoreKeyException("Il file non è un JSON valido: carica la chiave del service account così come l'hai scaricata.");
        }

        if (account is null || account.Type != "service_account")
            throw new InvalidStoreKeyException("Il JSON non è la chiave di un service account (manca \"type\": \"service_account\").");

        if (string.IsNullOrWhiteSpace(account.ClientEmail) || string.IsNullOrWhiteSpace(account.PrivateKey))
            throw new InvalidStoreKeyException("Nel JSON mancano client_email o private_key.");

        // Il token si chiede all'indirizzo scritto nel file: uno diverso da
        // quello di Google vorrebbe dire mandare la firma a qualcun altro.
        if (account.TokenUri is not null && account.TokenUri != DefaultTokenUri)
            throw new InvalidStoreKeyException("Il token_uri del JSON non è quello di Google.");

        using var rsa = account.CreateSigner();
        return account;
    }

    public RSA CreateSigner()
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(PrivateKey);
            return rsa;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidStoreKeyException("La chiave privata nel JSON non è leggibile.");
        }
    }

    public string Fingerprint()
    {
        using var rsa = CreateSigner();
        return Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
    }
}
