namespace Flarelytics.Core.Database.Entities;

/// <summary>Cosa può fare un membro dentro un tenant. L'ordine conta: ogni ruolo include quelli prima.</summary>
public enum OrgRole
{
    /// <summary>Vede progetti e dati, non cambia niente.</summary>
    Viewer = 0,

    /// <summary>Gestisce progetti e credenziali.</summary>
    Admin = 1,

    /// <summary>Come admin, più abbonamento e cancellazione del tenant.</summary>
    Owner = 2
}

/// <summary>L'appartenenza di un utente a un tenant, con il suo ruolo.</summary>
/// <remarks>
/// Come <see cref="Tenant"/>, non è filtrata sul tenant corrente: è proprio
/// la tabella da cui si decide se un tenant si può aprire.
/// </remarks>
public class Membership : BaseEntity
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public OrgRole Role { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private Membership() { }

    public static Membership Create(Guid tenantId, Guid userId, OrgRole role) =>
        new() { TenantId = tenantId, UserId = userId, Role = role };

    public void ChangeRole(OrgRole role) => Role = role;
}
