using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Orgs;

/// <summary>
/// L'organizzazione della richiesta, il ruolo che ci ha l'utente e cosa può
/// vedere (progetti e sezioni). Valorizzata da <see cref="OrgAccessFilter"/>.
/// </summary>
public class CurrentOrg
{
    public Guid TenantId { get; private set; }
    public OrgRole Role { get; private set; }
    public MemberAccess Access { get; private set; } = MemberAccess.Full;

    /// <summary>Vede tutti i progetti e tutte le sezioni.</summary>
    public bool IsFull => Access.IsFull;

    internal void Set(Guid tenantId, OrgRole role, MemberAccess access)
    {
        TenantId = tenantId;
        Role = role;
        Access = access;
    }

    /// <summary>
    /// Il progetto si vede. Null (un post o un dato dell'organizzazione, senza
    /// progetto) lo vede solo chi vede tutti i progetti.
    /// </summary>
    public bool CanSee(Guid? projectId) => Access.AllProjects || projectId is { } id && Access.ProjectIds.Contains(id);

    public bool Has(AppSections section) => (Access.Sections & section) == section;

    /// <summary>I progetti visibili, per i filtri delle query; null = tutti.</summary>
    public IReadOnlyList<Guid>? VisibleProjects => Access.AllProjects ? null : Access.ProjectIds;

    /// <summary>Il progetto deve essere visibile: altrimenti 404, come se non esistesse.</summary>
    public void EnsureCanSee(Guid? projectId)
    {
        if (!CanSee(projectId)) throw ApiProblem.NotFound("Progetto");
    }
}

/// <summary>Il ruolo minimo che una rotta richiede. Senza, basta essere membri.</summary>
public sealed record RequiredOrgRole(OrgRole Role);

/// <summary>La sezione a cui appartiene una rotta (Store o Social): senza, il membro non la apre.</summary>
public sealed record RequiredSection(AppSections Section);

/// <summary>
/// La rotta gestisce l'organizzazione intera (membri, chiavi, account social,
/// progetti): serve vedere tutti i progetti e tutte le sezioni.
/// </summary>
public sealed record RequiresFullAccess;

/// <summary>
/// La porta di tutte le rotte <c>/orgs/{orgId}</c>: controlla che l'utente ne
/// faccia parte con il ruolo richiesto, e solo allora imposta il tenant.
/// </summary>
/// <remarks>
/// <para>È l'unico posto, nell'API, in cui si chiama
/// <see cref="TenantContext.Set"/>, a parte la registrazione. Da qui in poi i
/// filtri di EF e la Row-Level Security vedono solo i dati di questo tenant,
/// quindi un handler non può leggere quelli di un altro nemmeno sbagliando
/// l'id.</para>
///
/// <para>Un tenant di cui non si è membri risponde 404 e non 403: un 403
/// direbbe che quell'id esiste. Lo stesso per un progetto (<c>{projectId}</c>
/// nella rotta) che il membro non vede.</para>
///
/// <para>Oltre al ruolo controlla la sezione della rotta
/// (<see cref="RequiredSection"/>) e se serve l'accesso completo
/// (<see cref="RequiresFullAccess"/>). Le liste che mescolano progetti le
/// filtra ogni handler con <see cref="CurrentOrg.VisibleProjects"/>.</para>
/// </remarks>
public class OrgAccessFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (!Guid.TryParse(http.GetRouteValue("orgId") as string, out var orgId))
        {
            throw ApiProblem.NotFound("Organizzazione");
        }

        var userId = http.User.UserId();
        var db = http.RequestServices.GetRequiredService<FlarelyticsDbContext>();

        var membership = await db.Set<Membership>().AsNoTracking()
            .SingleOrDefaultAsync(m => m.TenantId == orgId && m.UserId == userId, http.RequestAborted)
            ?? throw ApiProblem.NotFound("Organizzazione");

        var metadata = http.GetEndpoint()?.Metadata;
        var required = metadata?.GetMetadata<RequiredOrgRole>()?.Role ?? OrgRole.Viewer;
        if (membership.Role < required)
        {
            throw ApiProblem.Forbidden("Il tuo ruolo in questa organizzazione non permette questa operazione.");
        }

        var access = membership.Access;
        if (metadata?.GetMetadata<RequiredSection>() is { } section && (access.Sections & section.Section) != section.Section)
        {
            throw ApiProblem.Forbidden(section.Section == AppSections.Store
                ? "Non hai accesso alla sezione Store di questa organizzazione."
                : "Non hai accesso alla sezione Social di questa organizzazione.");
        }
        if (metadata?.GetMetadata<RequiresFullAccess>() is not null && !access.IsFull)
        {
            throw ApiProblem.Forbidden("Serve l'accesso a tutti i progetti e a tutte le sezioni dell'organizzazione.");
        }

        var current = http.RequestServices.GetRequiredService<CurrentOrg>();
        current.Set(orgId, membership.Role, access);
        if (http.GetRouteValue("projectId") is string raw && Guid.TryParse(raw, out var projectId) && !current.CanSee(projectId))
        {
            throw ApiProblem.NotFound("Progetto");
        }

        http.RequestServices.GetRequiredService<TenantContext>().Set(orgId);

        return await next(context);
    }
}

public static class OrgAccessExtensions
{
    public static TBuilder RequireOrgRole<TBuilder>(this TBuilder builder, OrgRole role)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredOrgRole(role));

    public static TBuilder RequireSection<TBuilder>(this TBuilder builder, AppSections section)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredSection(section));

    /// <summary>Solo per chi vede tutta l'organizzazione (vedi <see cref="RequiresFullAccess"/>).</summary>
    public static TBuilder RequireFullAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresFullAccess());

    /// <summary>Il gruppo <c>/orgs/{orgId}</c>, con autenticazione e controllo di appartenenza già applicati.</summary>
    public static RouteGroupBuilder MapOrgGroup(this IEndpointRouteBuilder api, string prefix = "") =>
        api.MapGroup("/orgs/{orgId:guid}" + prefix)
            .RequireAuthorization()
            .AddEndpointFilter<OrgAccessFilter>();
}
