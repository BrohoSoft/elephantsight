using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Flarelytics.Core.Database.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flarelytics.Core.Stores;

/// <summary>
/// App Store Connect API, autenticata con un JWT firmato dalla chiave .p8.
/// </summary>
/// <remarks>
/// Il JWT non si chiede a nessuno: lo firmiamo noi a ogni uso e Apple lo
/// verifica con la chiave pubblica che conosce. Dura al massimo 20 minuti per
/// regola di Apple; qui 10, che bastano a qualunque chiamata.
/// </remarks>
public class AppStoreConnectGateway(HttpClient http) : IStoreGateway
{
    public const string BaseAddress = "https://api.appstoreconnect.apple.com/";

    public Store Store => Store.AppStore;

    public async Task<VerificationResult> VerifyAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        using var request = Request(credential, secret, "v1/apps?limit=1&fields[apps]=name");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return new(VerificationOutcome.Unreachable, "App Store Connect non risponde: riprova fra qualche minuto.");
        }

        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.OK => VerificationResult.Ok,

                HttpStatusCode.Unauthorized => new(VerificationOutcome.Rejected,
                    "Apple non riconosce la chiave: controlla Key ID e Issuer ID, e che la chiave non sia stata revocata."),

                HttpStatusCode.Forbidden => new(VerificationOutcome.Limited,
                    "La chiave è valida ma il suo ruolo non basta per leggere le app: generane una del team con accesso Sales."),

                _ => new(VerificationOutcome.Unreachable, $"App Store Connect ha risposto {(int)response.StatusCode}: riprova fra qualche minuto.")
            };
        }
    }

    public async Task<IReadOnlyList<StoreAppInfo>> ListAppsAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        var apps = new List<StoreAppInfo>();
        string? next = "v1/apps?limit=200&fields[apps]=name,bundleId&sort=name";

        // Apple pagina con un link assoluto in links.next: si segue finché c'è.
        while (next is not null)
        {
            using var request = Request(credential, secret, next);
            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
                throw new StoreAccessException($"App Store Connect ha risposto {(int)response.StatusCode} alla richiesta delle app.");

            var page = await response.Content.ReadFromJsonAsync<AppsPage>(ct)
                ?? throw new StoreAccessException("App Store Connect ha risposto con un corpo vuoto.");

            apps.AddRange(page.Data.Select(a => new StoreAppInfo(a.Id, a.Attributes.Name ?? a.Id, a.Attributes.BundleId ?? "")));
            next = page.Links?.Next;
        }

        return apps;
    }

    private static HttpRequestMessage Request(StoreCredential credential, ReadOnlyMemory<byte> secret, string uri)
    {
        var key = AppleKey.Parse(
            credential.AppleKeyId!, credential.AppleIssuerId!, credential.AppleVendorNumber,
            Encoding.UTF8.GetString(secret.Span));

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key, DateTime.UtcNow));
        return request;
    }

    public static string CreateToken(AppleKey key, DateTime nowUtc)
    {
        using var ecdsa = key.CreateSigner();

        // JsonWebTokenHandler firma ES256 nel formato JWS (R||S da 64 byte),
        // che è quello che Apple vuole. La firma DER che darebbe ECDsa.SignData
        // con le impostazioni predefinite verrebbe rifiutata.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = key.IssuerId,
            Audience = "appstoreconnect-v1",
            IssuedAt = nowUtc,
            Expires = nowUtc.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(ecdsa) { KeyId = key.KeyId }, SecurityAlgorithms.EcdsaSha256)
            {
                CryptoProviderFactory = UncachedSigning
            }
        });
    }

    /// <summary>
    /// Firme senza cache. IdentityModel, di suo, tiene in cache chi firma
    /// associandolo al Key ID: alla seconda firma con la stessa chiave riusa
    /// l'oggetto della prima, che però qui è già stato eliminato dopo l'uso
    /// (la chiave privata non resta in memoria fra una chiamata e l'altra).
    /// Il risultato era un ObjectDisposedException dal secondo token in poi.
    /// </summary>
    public static readonly CryptoProviderFactory UncachedSigning = new() { CacheSignatureProviders = false };

    private record AppsPage(List<AppResource> Data, PageLinks? Links);
    private record AppResource(string Id, AppAttributes Attributes);
    private record AppAttributes(string? Name, string? BundleId);
    private record PageLinks([property: JsonPropertyName("next")] string? Next);
}
