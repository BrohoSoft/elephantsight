using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Billing;

/// <summary>Cosa succede dopo aver chiesto un piano.</summary>
/// <param name="CheckoutUrl">
/// Dove mandare l'utente a pagare. Null quando il piano è già attivo, che oggi è
/// sempre: il frontend deve comunque gestire il caso valorizzato, perché sarà
/// quello normale con un provider vero.
/// </param>
public record CheckoutResult(Subscription Subscription, string? CheckoutUrl);

/// <summary>
/// Il punto in cui si attaccheranno i pagamenti veri.
/// </summary>
/// <remarks>
/// Oggi c'è solo <see cref="ManualBillingProvider"/>, che attiva tutto subito.
/// Il giorno di Stripe (o di un merchant of record come Paddle) si aggiunge
/// un'implementazione che restituisce un <c>CheckoutUrl</c> e un endpoint per
/// i suoi webhook, che chiamerà <c>Subscription.Activate</c> a pagamento
/// riuscito. Il resto dell'applicazione guarda solo lo stato della
/// <see cref="Subscription"/> e non deve cambiare.
/// </remarks>
public interface IBillingProvider
{
    string Name { get; }

    /// <summary>Crea l'abbonamento di un tenant nuovo. Non salva: lo fa il chiamante.</summary>
    Task<CheckoutResult> StartAsync(Guid tenantId, Plan plan, CancellationToken ct);

    /// <summary>
    /// Cambia piano a un abbonamento esistente, o lo riattiva se era annullato.
    /// Non salva.
    /// </summary>
    Task<CheckoutResult> ChangePlanAsync(Subscription subscription, Plan plan, CancellationToken ct);

    /// <summary>
    /// Annulla l'abbonamento. Con un provider vero l'annullamento varrebbe a
    /// fine periodo pagato; con quello manuale vale subito. Non salva.
    /// </summary>
    Task CancelAsync(Subscription subscription, CancellationToken ct);
}

/// <summary>
/// Pagamenti in sordina: ogni piano risulta pagato appena scelto, e non scade.
/// </summary>
public class ManualBillingProvider : IBillingProvider
{
    public string Name => "manual";

    public Task<CheckoutResult> StartAsync(Guid tenantId, Plan plan, CancellationToken ct) =>
        Task.FromResult(new CheckoutResult(Subscription.Create(tenantId, plan.Code, Name), null));

    public Task<CheckoutResult> ChangePlanAsync(Subscription subscription, Plan plan, CancellationToken ct)
    {
        subscription.Activate(plan.Code, externalReference: null, periodEndUtc: null);
        return Task.FromResult(new CheckoutResult(subscription, null));
    }

    public Task CancelAsync(Subscription subscription, CancellationToken ct)
    {
        subscription.Cancel();
        return Task.CompletedTask;
    }
}
