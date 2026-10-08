namespace Flarelytics.Core.Database.Entities;

public enum SubscriptionStatus
{
    Active = 0,

    /// <summary>Pagamento non riuscito: i dati si vedono, le modifiche no.</summary>
    PastDue = 1,

    Canceled = 2
}

/// <summary>
/// L'abbonamento del tenant: uno per tenant.
/// </summary>
/// <remarks>
/// <see cref="Provider"/> e <see cref="ExternalReference"/> sono i due campi
/// che oggi restano vuoti o fissi e che il giorno dei pagamenti veri conterranno
/// "stripe" (o chi sarà) e l'id dell'abbonamento dal suo lato.
/// </remarks>
public class Subscription : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string PlanCode { get; private set; } = null!;
    public SubscriptionStatus Status { get; private set; }
    public string Provider { get; private set; } = null!;
    public string? ExternalReference { get; private set; }

    /// <summary>Fine del periodo pagato. Null con il provider manuale, che non scade.</summary>
    public DateTime? CurrentPeriodEndUtc { get; private set; }

    private Subscription() { }

    public static Subscription Create(Guid tenantId, string planCode, string provider) => new()
    {
        TenantId = tenantId,
        PlanCode = planCode,
        Provider = provider,
        Status = SubscriptionStatus.Active
    };

    public bool IsActive(DateTime nowUtc) =>
        Status == SubscriptionStatus.Active && (CurrentPeriodEndUtc is null || nowUtc < CurrentPeriodEndUtc);

    public void Activate(string planCode, string? externalReference, DateTime? periodEndUtc)
    {
        PlanCode = planCode;
        Status = SubscriptionStatus.Active;
        ExternalReference = externalReference;
        CurrentPeriodEndUtc = periodEndUtc;
    }

    public void MarkPastDue() => Status = SubscriptionStatus.PastDue;

    public void Cancel() => Status = SubscriptionStatus.Canceled;
}
