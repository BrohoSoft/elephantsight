namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Lo SKU di un'app Apple e il suo Apple ID.
/// </summary>
/// <remarks>
/// Nei report di vendita un acquisto in-app dice a quale app appartiene solo
/// con lo SKU dell'app ("Parent Identifier"), mentre le metriche sono per Apple
/// ID. La corrispondenza si impara dall'API e dalle righe dei report stessi, e
/// si salva: così un report si può rielaborare anche anni dopo, senza chiedere
/// niente ad Apple.
/// </remarks>
public class AppleAppSku : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid CredentialId { get; set; }
    public string Sku { get; set; } = null!;
    public string AppleId { get; set; } = null!;
}
