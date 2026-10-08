namespace Flarelytics.Core.Database.Entities;

public enum EmailTokenPurpose
{
    ConfirmEmail = 0,
    ResetPassword = 1
}

/// <summary>
/// Il token monouso dei link mandati per email: conferma dell'indirizzo e
/// recupero password. Come per le sessioni, a database c'è solo l'hash.
/// </summary>
public class EmailToken : BaseEntity
{
    public Guid UserId { get; private set; }
    public EmailTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }

    private EmailToken() { }

    public static EmailToken Issue(Guid userId, EmailTokenPurpose purpose, string token, DateTime nowUtc, TimeSpan lifetime) => new()
    {
        UserId = userId,
        Purpose = purpose,
        TokenHash = RefreshToken.Hash(token),
        ExpiresAtUtc = nowUtc.Add(lifetime)
    };

    public bool IsUsable(DateTime nowUtc) => UsedAtUtc is null && nowUtc < ExpiresAtUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc ??= nowUtc;
}
