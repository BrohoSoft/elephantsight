using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Auth;

/// <summary>
/// Il refresh token sta in un cookie HttpOnly, non nel corpo della risposta.
/// </summary>
/// <remarks>
/// <para>L'access token il frontend lo tiene in memoria e lo perde a ogni
/// ricarica della pagina: è il refresh a ridargliene uno. Se il refresh stesse
/// in localStorage, uno script iniettato nella pagina potrebbe leggerlo e
/// portarselo via, con 30 giorni di sessione. In un cookie HttpOnly JavaScript
/// non lo vede.</para>
///
/// <para><b>SameSite=Strict e Path ristretto</b> alle rotte di autenticazione:
/// il cookie non parte da siti terzi e non accompagna le altre chiamate.
/// Funziona perché frontend e API stanno sullo stesso dominio, dietro Caddy;
/// con domini diversi servirebbe SameSite=None e un controllo CSRF.</para>
/// </remarks>
public class RefreshCookie(IOptions<AuthOptions> options)
{
    public const string Name = "flarelytics_refresh";
    public const string Path = "/api/v1/auth";

    public string? Read(HttpRequest request) => request.Cookies[Name];

    public void Write(HttpResponse response, string token, DateTime nowUtc) =>
        response.Cookies.Append(Name, token, Options(nowUtc.Add(RefreshTokenService.Lifetime)));

    public void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(null));

    private CookieOptions Options(DateTime? expiresUtc) => new()
    {
        HttpOnly = true,
        Secure = options.Value.SecureCookies,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresUtc
    };
}
