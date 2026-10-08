namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Un prodotto del cliente, visto come una cosa sola anche se è pubblicato su
/// due store: al massimo un'app App Store e un'app Google Play.
/// </summary>
public class Project : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }

    private readonly List<ProjectApp> _apps = [];
    public IReadOnlyCollection<ProjectApp> Apps => _apps;

    private Project() { }

    public static Project Create(Guid tenantId, string name, string? description) => new()
    {
        TenantId = tenantId,
        Name = name.Trim(),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
    };

    public void Update(string name, string? description)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>
    /// Collega l'app dello store indicato, sostituendo quella che c'era: un
    /// progetto ne ha una per store, e il vincolo unico a database lo ribadisce.
    /// </summary>
    public ProjectApp LinkApp(StoreCredential credential, string externalAppId, string? displayName)
    {
        _apps.RemoveAll(a => a.Store == credential.Store);

        var app = ProjectApp.Create(this, credential, externalAppId, displayName);
        _apps.Add(app);
        return app;
    }

    public bool UnlinkApp(Store store) => _apps.RemoveAll(a => a.Store == store) > 0;
}
