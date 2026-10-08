using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flarelytics.Core.Stores;

/// <summary>
/// Il token di accesso di un service account Google (flusso "JWT bearer" di
/// OAuth 2.0): si firma un JWT con la chiave privata del JSON e lo si scambia
/// con Google.
/// </summary>
/// <remarks>Niente librerie Google: è una richiesta sola, e una dipendenza in meno da aggiornare.</remarks>
public static class GoogleAuth
{
    public static async Task<string> AccessTokenAsync(HttpClient http, ReadOnlyMemory<byte> secret, string scopes, CancellationToken ct)
    {
        var account = GoogleServiceAccount.Parse(Encoding.UTF8.GetString(secret.Span));
        var now = DateTime.UtcNow;

        string assertion;
        using (var rsa = account.CreateSigner())
        {
            assertion = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = account.ClientEmail,
                Audience = GoogleServiceAccount.DefaultTokenUri,
                IssuedAt = now,
                Expires = now.AddMinutes(30),
                Claims = new Dictionary<string, object> { ["scope"] = scopes },
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(rsa) { KeyId = account.PrivateKeyId }, SecurityAlgorithms.RsaSha256)
                {
                    // Vedi AppStoreConnectGateway.UncachedSigning.
                    CryptoProviderFactory = AppStoreConnectGateway.UncachedSigning
                }
            });
        }

        using var response = await http.PostAsync(GoogleServiceAccount.DefaultTokenUri, new FormUrlEncodedContent(
        [
            new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new("assertion", assertion)
        ]), ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new StoreAccessException(
                "Google non accetta la chiave del service account: probabilmente è stata cancellata o disattivata in Google Cloud.");
        }

        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return body?.AccessToken ?? throw new StoreAccessException("Google non ha restituito un token.");
    }

    private record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
}
