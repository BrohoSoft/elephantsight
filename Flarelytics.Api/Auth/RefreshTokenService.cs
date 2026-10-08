using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Auth;

/// <summary>
/// Sessioni a vita lunga, con rotazione a ogni uso.
/// </summary>
/// <remarks>
/// Il token speso resta a database revocato e con il riferimento al suo
/// successore. Se qualcuno lo ripresenta, di quel token esiste una copia: non
/// si sa quale delle due parti sia quella legittima, quindi si chiudono tutte
/// le sessioni dell'utente.
///
/// Nessun metodo salva: lo fa il chiamante, insieme al resto.
/// </remarks>
public class RefreshTokenService(FlarelyticsDbContext db)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public string Issue(User user, DateTime nowUtc)
    {
        var token = SecureToken.Create();
        db.Set<RefreshToken>().Add(RefreshToken.Issue(user.Id, token, nowUtc, Lifetime));
        return token;
    }

    /// <returns>L'utente e il token nuovo, o null se la sessione non è valida.</returns>
    public async Task<(User User, string Token)?> RotateAsync(string token, DateTime nowUtc, CancellationToken ct)
    {
        var hash = RefreshToken.Hash(token);
        var existing = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null) return null;

        if (existing.RevokedAtUtc is not null)
        {
            // Senza successore è stato chiuso da un logout: un client rimasto
            // indietro, non un furto. Con successore invece è stato speso, e
            // rivederlo vuol dire che ne gira una copia.
            if (existing.ReplacedByTokenId is not null) await RevokeAllAsync(existing.UserId, nowUtc, ct);
            return null;
        }

        if (nowUtc >= existing.ExpiresAtUtc) return null;

        var user = await db.Set<User>().FindAsync([existing.UserId], ct);
        if (user is null) return null;

        var value = SecureToken.Create();
        var next = RefreshToken.Issue(user.Id, value, nowUtc, Lifetime);
        db.Set<RefreshToken>().Add(next);
        existing.Revoke(nowUtc, next.Id);

        return (user, value);
    }

    public async Task RevokeAsync(string token, DateTime nowUtc, CancellationToken ct)
    {
        var hash = RefreshToken.Hash(token);
        var existing = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        existing?.Revoke(nowUtc);
    }

    /// <summary>Va chiamata a ogni cambio password: lo stamp invalida gli access token, ma non i refresh.</summary>
    public async Task RevokeAllAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var active = await db.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var t in active) t.Revoke(nowUtc);
    }
}
