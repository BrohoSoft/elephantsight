using System.Text.RegularExpressions;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Billing;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Billing;

/// <summary>
/// Abbonamento e fatturazione di un'organizzazione.
/// </summary>
/// <remarks>
/// Tutti possono vedere il riepilogo; cambiare piano, annullare e modificare
/// l'intestatario spetta all'owner. Nessuna rotta qui richiede un abbonamento
/// attivo: è proprio da qui che lo si riattiva.
/// </remarks>
public static partial class BillingEndpoints
{
    public static void MapBilling(this IEndpointRouteBuilder api)
    {
        var org = api.MapOrgGroup();

        org.MapGet("/billing", Overview);
        org.MapPut("/billing/profile", UpdateProfile).RequireOrgRole(OrgRole.Owner).Validating<BillingProfileRequest>();

        org.MapGet("/subscription", GetSubscription);
        org.MapPut("/subscription", ChangePlan).RequireOrgRole(OrgRole.Owner).Validating<ChangePlanRequest>();
        org.MapPost("/subscription/cancel", Cancel).RequireOrgRole(OrgRole.Owner);
    }

    /// <summary>Tutto quello che mostra la pagina Fatturazione, in una chiamata.</summary>
    private static async Task<IResult> Overview(CurrentOrg org, FlarelyticsDbContext db, IBillingProvider billing, CancellationToken ct)
    {
        var subscription = await db.Set<Subscription>().AsNoTracking().SingleAsync(ct);
        var projects = await db.Set<Project>().CountAsync(ct);
        var members = await db.Set<Membership>().CountAsync(m => m.TenantId == org.TenantId, ct);
        var profile = await db.Set<BillingProfile>().AsNoTracking().SingleOrDefaultAsync(ct);

        return Results.Ok(new BillingOverview(
            SubscriptionResponse.From(subscription, projects),
            members,
            billing.Name,
            profile is null ? null : BillingProfileResponse.From(profile)));
    }

    private static async Task<IResult> UpdateProfile(
        BillingProfileRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var profile = await db.Set<BillingProfile>().SingleOrDefaultAsync(ct);
        if (profile is null)
        {
            profile = BillingProfile.Create(org.TenantId);
            db.Add(profile);
        }

        profile.Update(req.CompanyName, req.VatNumber, req.TaxCode, req.AddressLine, req.City, req.PostalCode,
            req.Province, req.CountryCode, req.BillingEmail, req.SdiCode, req.Pec);
        await db.SaveChangesAsync(ct);

        return Results.Ok(BillingProfileResponse.From(profile));
    }

    private static async Task<IResult> GetSubscription(FlarelyticsDbContext db, CancellationToken ct)
    {
        var subscription = await db.Set<Subscription>().AsNoTracking().SingleAsync(ct);
        var projects = await db.Set<Project>().CountAsync(ct);
        return Results.Ok(SubscriptionResponse.From(subscription, projects));
    }

    /// <summary>
    /// Cambia piano, o riattiva un abbonamento annullato. Un piano più piccolo
    /// dei progetti che si hanno già si rifiuta: prima si cancellano quelli in
    /// più, poi si scende.
    /// </summary>
    private static async Task<IResult> ChangePlan(
        ChangePlanRequest req, FlarelyticsDbContext db, IBillingProvider billing, CancellationToken ct)
    {
        var plan = Plans.Find(req.Plan)!;
        var projects = await db.Set<Project>().CountAsync(ct);

        if (plan.MaxProjects is { } max && projects > max)
        {
            throw ApiProblem.Conflict("plan_too_small",
                $"Il piano {plan.Name} permette {max} progetti e ne hai {projects}: cancellane alcuni prima di cambiare.");
        }

        var subscription = await db.Set<Subscription>().SingleAsync(ct);
        var checkout = await billing.ChangePlanAsync(subscription, plan, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(SubscriptionResponse.From(checkout.Subscription, projects) with { CheckoutUrl = checkout.CheckoutUrl });
    }

    /// <summary>
    /// Annulla l'abbonamento. I dati restano e si continuano a vedere; le
    /// modifiche si bloccano finché non si sceglie di nuovo un piano.
    /// </summary>
    private static async Task<IResult> Cancel(FlarelyticsDbContext db, IBillingProvider billing, CancellationToken ct)
    {
        var subscription = await db.Set<Subscription>().SingleAsync(ct);
        await billing.CancelAsync(subscription, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(SubscriptionResponse.From(subscription, await db.Set<Project>().CountAsync(ct)));
    }

    [GeneratedRegex(@"^(IT)?\d{11}$")]
    internal static partial Regex ItalianVat();

    [GeneratedRegex(@"^[A-Z0-9]{7}$")]
    internal static partial Regex SdiCode();

    [GeneratedRegex(@"^([A-Z0-9]{16}|\d{11})$")]
    internal static partial Regex ItalianTaxCode();
}

public record ChangePlanRequest(string Plan);

public record SubscriptionResponse(
    Plan Plan, SubscriptionStatus Status, bool IsActive, DateTime? CurrentPeriodEndUtc, int ProjectCount, string? CheckoutUrl)
{
    public static SubscriptionResponse From(Subscription s, int projects) => new(
        Plans.Resolve(s.PlanCode), s.Status, s.IsActive(DateTime.UtcNow), s.CurrentPeriodEndUtc, projects, null);
}

/// <param name="Provider">Chi incassa: oggi <c>manual</c>, cioè nessuno.</param>
public record BillingOverview(SubscriptionResponse Subscription, int MemberCount, string Provider, BillingProfileResponse? Profile);

public record BillingProfileRequest(
    string CompanyName, string? VatNumber, string? TaxCode, string AddressLine, string City, string PostalCode,
    string? Province, string CountryCode, string BillingEmail, string? SdiCode, string? Pec);

public record BillingProfileResponse(
    string CompanyName, string? VatNumber, string? TaxCode, string AddressLine, string City, string PostalCode,
    string? Province, string CountryCode, string BillingEmail, string? SdiCode, string? Pec)
{
    public static BillingProfileResponse From(BillingProfile p) => new(
        p.CompanyName, p.VatNumber, p.TaxCode, p.AddressLine, p.City, p.PostalCode, p.Province, p.CountryCode,
        p.BillingEmail, p.SdiCode, p.Pec);
}

public class ChangePlanRequestValidator : AbstractValidator<ChangePlanRequest>
{
    public ChangePlanRequestValidator() =>
        RuleFor(x => x.Plan).NotEmpty().Must(p => Plans.Find(p) is not null).WithMessage("Piano sconosciuto.");
}

/// <summary>
/// Le regole valgono per tutti; quelle italiane solo con paese IT. Per
/// un'azienda italiana con partita IVA la fattura elettronica deve sapere dove
/// arrivare: serve il codice destinatario SDI o la PEC.
/// </summary>
public class BillingProfileRequestValidator : AbstractValidator<BillingProfileRequest>
{
    public BillingProfileRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().WithMessage("Indica la ragione sociale o il nome dell'intestatario.").MaximumLength(200);
        RuleFor(x => x.AddressLine).NotEmpty().WithMessage("Indica l'indirizzo.").MaximumLength(200);
        RuleFor(x => x.City).NotEmpty().WithMessage("Indica la città.").MaximumLength(100);
        RuleFor(x => x.PostalCode).NotEmpty().WithMessage("Indica il CAP.").MaximumLength(20);
        RuleFor(x => x.CountryCode).NotEmpty().Length(2).Matches("^[A-Za-z]{2}$").WithMessage("Il paese è un codice di due lettere, per esempio IT.");
        RuleFor(x => x.BillingEmail).NotEmpty().EmailAddress().WithMessage("Indica un'email valida per ricevere le fatture.").MaximumLength(255);
        RuleFor(x => x.Pec).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Pec)).WithMessage("La PEC non è un indirizzo valido.").MaximumLength(255);
        RuleFor(x => x.VatNumber).MaximumLength(20);
        RuleFor(x => x.TaxCode).MaximumLength(20);
        RuleFor(x => x.Province).MaximumLength(5);

        When(x => IsItaly(x.CountryCode), () =>
        {
            RuleFor(x => x.VatNumber)
                .Must(v => BillingEndpoints.ItalianVat().IsMatch(v!.Trim().ToUpperInvariant()))
                .When(x => !string.IsNullOrWhiteSpace(x.VatNumber))
                .WithMessage("La partita IVA italiana ha 11 cifre.");
            RuleFor(x => x.TaxCode)
                .Must(v => BillingEndpoints.ItalianTaxCode().IsMatch(v!.Trim().ToUpperInvariant()))
                .When(x => !string.IsNullOrWhiteSpace(x.TaxCode))
                .WithMessage("Il codice fiscale ha 16 caratteri (persona) o 11 cifre (azienda).");
            RuleFor(x => x.SdiCode)
                .Must(v => BillingEndpoints.SdiCode().IsMatch(v!.Trim().ToUpperInvariant()))
                .When(x => !string.IsNullOrWhiteSpace(x.SdiCode))
                .WithMessage("Il codice destinatario SDI ha 7 caratteri.");
            RuleFor(x => x)
                .Must(x => !string.IsNullOrWhiteSpace(x.SdiCode) || !string.IsNullOrWhiteSpace(x.Pec))
                .When(x => !string.IsNullOrWhiteSpace(x.VatNumber))
                .WithName("SdiCode")
                .WithMessage("Per la fattura elettronica serve il codice destinatario SDI oppure la PEC.");
            RuleFor(x => x)
                .Must(x => !string.IsNullOrWhiteSpace(x.VatNumber) || !string.IsNullOrWhiteSpace(x.TaxCode))
                .WithName("TaxCode")
                .WithMessage("Serve la partita IVA o il codice fiscale.");
        });
    }

    private static bool IsItaly(string? country) => string.Equals(country?.Trim(), "IT", StringComparison.OrdinalIgnoreCase);
}
