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

/// <summary>Le parti del pannello, da accendere o spegnere per membro.</summary>
[Flags]
public enum AppSections
{
    None = 0,

    /// <summary>Le app sugli store: dashboard, recensioni, versioni, scheda, build, file di firma, chiavi degli store.</summary>
    Store = 1,

    /// <summary>Calendario, post ricorrenti, coda, account social.</summary>
    Social = 2,

    All = Store | Social
}

/// <summary>
/// Cosa vede un membro oltre al suo ruolo: tutti i progetti o solo alcuni, e
/// quali sezioni. Il ruolo dice cosa può fare, questo dove.
/// </summary>
/// <param name="ProjectIds">Solo con <paramref name="AllProjects"/> falso. Un progetto cancellato sparisce da solo (non si trova più).</param>
public sealed record MemberAccess(bool AllProjects, IReadOnlyList<Guid> ProjectIds, AppSections Sections)
{
    public static readonly MemberAccess Full = new(true, [], AppSections.All);

    /// <summary>Vede tutto: l'unico modo di gestire l'organizzazione (membri, chiavi, account social, progetti).</summary>
    public bool IsFull => AllProjects && Sections == AppSections.All;

    public MemberAccess Normalized() => AllProjects ? this with { ProjectIds = [] } : this with { ProjectIds = ProjectIds.Distinct().ToList() };
}

/// <summary>L'appartenenza di un utente a un tenant, con il suo ruolo e cosa può vedere.</summary>
/// <remarks>
/// Come <see cref="Tenant"/>, non è filtrata sul tenant corrente: è proprio
/// la tabella da cui si decide se un tenant si può aprire.
/// </remarks>
public class Membership : BaseEntity
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public OrgRole Role { get; private set; }

    /// <summary>Tutti i progetti (anche quelli creati dopo), o solo <see cref="ProjectIds"/>.</summary>
    public bool AllProjects { get; private set; } = true;

    public List<Guid> ProjectIds { get; private set; } = [];

    public AppSections Sections { get; private set; } = AppSections.All;

    public Tenant Tenant { get; private set; } = null!;

    public MemberAccess Access => new(AllProjects, ProjectIds, Sections);

    private Membership() { }

    public static Membership Create(Guid tenantId, Guid userId, OrgRole role, MemberAccess? access = null)
    {
        var m = new Membership { TenantId = tenantId, UserId = userId, Role = role };
        m.SetAccess(access ?? MemberAccess.Full);
        return m;
    }

    public void ChangeRole(OrgRole role)
    {
        Role = role;
        // Un owner vede sempre tutto: è lui che gestisce l'organizzazione.
        if (role == OrgRole.Owner) SetAccess(MemberAccess.Full);
    }

    public void SetAccess(MemberAccess access)
    {
        var a = Role == OrgRole.Owner ? MemberAccess.Full : access.Normalized();
        (AllProjects, ProjectIds, Sections) = (a.AllProjects, a.ProjectIds.ToList(), a.Sections);
    }

    public void ForgetProject(Guid projectId) => ProjectIds = ProjectIds.Where(id => id != projectId).ToList();
}
