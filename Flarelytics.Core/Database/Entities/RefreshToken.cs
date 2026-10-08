using System.Security.Cryptography;
using System.Text;

namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Una sessione a vita lunga. A database c'è solo l'hash del token: chi legge
/// la tabella non può aprire sessioni.
/// </summary>
public class RefreshToken : BaseEntity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>
    /// Il token che ha preso il posto di questo in una rotazione. È ciò che
    /// distingue un token speso (successore presente: rigiocarlo è un furto)
    /// da uno chiuso con il logout (nessun successore: è solo un client
    /// rimasto indietro).
    /// </summary>
    public Guid? ReplacedByTokenId { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(Guid userId, string token, DateTime nowUtc, TimeSpan lifetime) => new()
    {
        UserId = userId,
        TokenHash = Hash(token),
        ExpiresAtUtc = nowUtc.Add(lifetime)
    };

    public bool IsUsable(DateTime nowUtc) => RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    public void Revoke(DateTime nowUtc, Guid? replacedBy = null)
    {
        RevokedAtUtc ??= nowUtc;
        ReplacedByTokenId ??= replacedBy;
    }

    /// <summary>
    /// SHA-256 e non BCrypt: il token ha 256 bit casuali, non si indovina, e
    /// un hash lento qui costerebbe a ogni rinnovo senza proteggere niente.
    /// </summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
