namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// L'invito a entrare in un'organizzazione, mandato per email.
/// </summary>
/// <remarks>
/// <para>Non è <see cref="ITenantOwned"/>, come <see cref="Membership"/>: chi
/// lo accetta non fa ancora parte del tenant e lo trova solo attraverso il
/// token del link. Le rotte di gestione filtrano per tenant a mano, e lo fanno
/// sempre partendo dall'organizzazione già verificata dal filtro di
/// accesso.</para>
///
/// <para>Vale solo per l'indirizzo a cui è stato mandato: un link inoltrato a
/// qualcun altro non lo fa entrare.</para>
/// </remarks>
public class Invitation : BaseEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = null!;
    public OrgRole Role { get; private set; }
    public Guid InvitedByUserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private Invitation() { }

    public static Invitation Create(Guid tenantId, string email, OrgRole role, Guid invitedBy, string token, DateTime nowUtc) => new()
    {
        TenantId = tenantId,
        Email = User.NormalizeEmail(email),
        Role = role,
        InvitedByUserId = invitedBy,
        TokenHash = RefreshToken.Hash(token),
        ExpiresAtUtc = nowUtc.Add(Lifetime)
    };

    public bool IsPending(DateTime nowUtc) => AcceptedAtUtc is null && RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    public void Accept(DateTime nowUtc) => AcceptedAtUtc ??= nowUtc;

    public void Revoke(DateTime nowUtc) => RevokedAtUtc ??= nowUtc;
}
