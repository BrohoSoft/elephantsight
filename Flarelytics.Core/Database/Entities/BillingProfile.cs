namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// L'intestatario delle fatture di un tenant. Uno per tenant, facoltativo
/// finché i pagamenti sono in sordina.
/// </summary>
/// <remarks>
/// <see cref="SdiCode"/> e <see cref="Pec"/> esistono per la fattura
/// elettronica italiana: a un'azienda italiana la fattura arriva attraverso il
/// Sistema di Interscambio, al codice destinatario o alla PEC. Per un cliente
/// estero restano vuoti.
/// </remarks>
public class BillingProfile : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string CompanyName { get; private set; } = null!;
    public string? VatNumber { get; private set; }
    public string? TaxCode { get; private set; }
    public string AddressLine { get; private set; } = null!;
    public string City { get; private set; } = null!;
    public string PostalCode { get; private set; } = null!;
    public string? Province { get; private set; }
    public string CountryCode { get; private set; } = null!;
    public string BillingEmail { get; private set; } = null!;
    public string? SdiCode { get; private set; }
    public string? Pec { get; private set; }

    private BillingProfile() { }

    public static BillingProfile Create(Guid tenantId) => new() { TenantId = tenantId };

    public void Update(
        string companyName, string? vatNumber, string? taxCode, string addressLine, string city, string postalCode,
        string? province, string countryCode, string billingEmail, string? sdiCode, string? pec)
    {
        CompanyName = companyName.Trim();
        VatNumber = Clean(vatNumber)?.ToUpperInvariant();
        TaxCode = Clean(taxCode)?.ToUpperInvariant();
        AddressLine = addressLine.Trim();
        City = city.Trim();
        PostalCode = postalCode.Trim();
        Province = Clean(province)?.ToUpperInvariant();
        CountryCode = countryCode.Trim().ToUpperInvariant();
        BillingEmail = User.NormalizeEmail(billingEmail);
        SdiCode = Clean(sdiCode)?.ToUpperInvariant();
        Pec = Clean(pec) is { } p ? User.NormalizeEmail(p) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
