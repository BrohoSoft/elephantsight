using System.Security.Cryptography;
using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Secrets;

/// <summary>
/// L'unico modo di usare il segreto di una credenziale: lo decifra, lo passa
/// alla funzione e lo azzera, qualunque cosa succeda.
/// </summary>
/// <remarks>
/// Serve a non dover ricordare l'azzeramento in ogni punto che usa una chiave.
/// Il segreto resta in memoria solo per la durata della chiamata allo store.
/// </remarks>
public class CredentialSecrets(SecretVault vault)
{
    public async Task<T> UseAsync<T>(
        StoreCredential credential, Func<ReadOnlyMemory<byte>, Task<T>> action, CancellationToken ct)
    {
        byte[] secret;
        try
        {
            secret = await vault.ReadAsync(credential.TenantId, credential.Id, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or CryptographicException)
        {
            // Il file manca o non si decifra: un ripristino del database senza
            // il volume dei segreti, o senza la chiave master giusta. Non è un
            // problema dello store, ma per chi usa il pannello si risolve allo
            // stesso modo: ricaricando la chiave.
            throw new Stores.StoreAccessException(
                "Il file cifrato di questa chiave non si legge (manca, o la chiave master è cambiata): carica di nuovo la chiave.");
        }

        try
        {
            return await action(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
