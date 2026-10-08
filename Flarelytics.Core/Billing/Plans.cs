namespace Flarelytics.Core.Billing;

/// <param name="Code">Quello che si salva su <c>Subscription.PlanCode</c>. Non si cambia mai, una volta usato.</param>
/// <param name="MaxProjects">Null vuol dire senza limite.</param>
/// <param name="MonthlyPriceCents">Solo da mostrare, finché i pagamenti non sono veri.</param>
public record Plan(string Code, string Name, int? MaxProjects, int MonthlyPriceCents);

/// <summary>
/// I piani in vendita. Stanno nel codice e non nel database: cambiano con un
/// rilascio, insieme ai controlli che li fanno rispettare.
/// </summary>
public static class Plans
{
    public static readonly Plan Starter = new("starter", "Starter", MaxProjects: 3, MonthlyPriceCents: 900);
    public static readonly Plan Pro = new("pro", "Pro", MaxProjects: 20, MonthlyPriceCents: 2900);
    public static readonly Plan Business = new("business", "Business", MaxProjects: null, MonthlyPriceCents: 9900);

    public static readonly IReadOnlyList<Plan> All = [Starter, Pro, Business];

    public static Plan? Find(string code) => All.FirstOrDefault(p => p.Code == code);

    /// <summary>
    /// Per gli abbonamenti già salvati: un codice che non c'è più vuol dire un
    /// piano tolto dal listino, e chi lo aveva resta con i limiti più bassi
    /// invece di far fallire ogni richiesta.
    /// </summary>
    public static Plan Resolve(string code) => Find(code) ?? Starter;
}
