namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Il passo intermedio di un accesso con 2FA: password giusta, codice ancora
/// da dare.
/// </summary>
/// <remarks>
/// Sta a database e non in un token firmato perché deve contare i tentativi:
/// un codice da 6 cifre si indovina, se si può provare all'infinito. Dopo
/// <see cref="MaxAttempts"/> errori la sfida si brucia e serve rifare il login
/// con la password.
/// </remarks>
public class LoginChallenge : BaseEntity
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAtUtc { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private LoginChallenge() { }

    public static LoginChallenge Issue(Guid userId, string token, DateTime nowUtc) => new()
    {
        UserId = userId,
        TokenHash = RefreshToken.Hash(token),
        ExpiresAtUtc = nowUtc.Add(Lifetime)
    };

    public bool IsUsable(DateTime nowUtc) =>
        CompletedAtUtc is null && FailedAttempts < MaxAttempts && nowUtc < ExpiresAtUtc;

    public void RecordFailure() => FailedAttempts++;

    public void Complete(DateTime nowUtc) => CompletedAtUtc ??= nowUtc;
}
