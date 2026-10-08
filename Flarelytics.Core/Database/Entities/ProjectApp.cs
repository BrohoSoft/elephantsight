namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Il collegamento fra un progetto e un'app su uno store, attraverso una
/// credenziale del tenant.
/// </summary>
/// <remarks>
/// <see cref="ExternalAppId"/> è l'identificativo con cui lo store conosce
/// l'app: l'Apple ID numerico per l'App Store (non il bundle id: i report di
/// Apple usano quello), il package name per Google Play.
/// </remarks>
public class ProjectApp : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Store Store { get; private set; }
    public Guid CredentialId { get; private set; }
    public string ExternalAppId { get; private set; } = null!;
    public string? DisplayName { get; private set; }

    public StoreCredential Credential { get; private set; } = null!;

    private ProjectApp() { }

    internal static ProjectApp Create(Project project, StoreCredential credential, string externalAppId, string? displayName) => new()
    {
        TenantId = project.TenantId,
        ProjectId = project.Id,
        Store = credential.Store,
        CredentialId = credential.Id,
        Credential = credential,
        ExternalAppId = externalAppId.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()
    };
}
