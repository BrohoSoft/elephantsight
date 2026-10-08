using System.ComponentModel.DataAnnotations;

namespace Flarelytics.Api.Auth;

/// <summary>Firma degli access token. Sezione <c>Jwt</c>.</summary>
public class JwtOptions
{
    public const string Section = "Jwt";

    [Required] public string Issuer { get; set; } = null!;
    [Required] public string Audience { get; set; } = null!;

    /// <summary>Chiave HMAC: almeno 32 caratteri, cioè almeno i 256 bit che HS256 richiede.</summary>
    [Required, MinLength(32)] public string Key { get; set; } = null!;
}

/// <summary>Comportamento delle sessioni web. Sezione <c>Auth</c>.</summary>
public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Il cookie del refresh token viaggia solo su HTTPS. Si spegne solo in
    /// sviluppo e nei test, dove si parla in HTTP con localhost.
    /// </summary>
    public bool SecureCookies { get; set; } = true;

    /// <summary>L'indirizzo del frontend, per costruire i link delle email.</summary>
    [Required] public string PublicAppUrl { get; set; } = null!;

    /// <summary>Tentativi al minuto per IP sulle rotte anonime di autenticazione.</summary>
    public int AuthRequestsPerMinute { get; set; } = 20;
}
