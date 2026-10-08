using System.Text;
using Flarelytics.Core.Database.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Flarelytics.Api.Auth;

/// <summary>Emette gli access token: JWT brevi, firmati HS256.</summary>
public class TokenService(IOptions<JwtOptions> options)
{
    /// <summary>
    /// Breve di proposito: la sessione lunga la tiene il refresh token, che è
    /// revocabile riga per riga. Un access token rubato smette di valere da sé.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Claim con il <see cref="User.SecurityStamp"/>: se cambia, il token non vale più.</summary>
    public const string StampClaim = "stamp";

    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) Create(User user, DateTime nowUtc)
    {
        var expires = nowUtc.Add(Lifetime);

        // Nessun tenant nel token: un utente può stare in più organizzazioni e
        // quella su cui lavora la dice la rotta (/orgs/{orgId}). Il permesso si
        // controlla a ogni richiesta sulla tabella delle membership, quindi un
        // membro rimosso perde l'accesso subito e non alla scadenza del token.
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Email] = user.Email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [StampClaim] = user.SecurityStamp
            }
        });

        return (token, expires);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.Key));
}
