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
        var secret = await vault.ReadAsync(credential.TenantId, credential.Id, ct);
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
