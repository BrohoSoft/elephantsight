using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Orgs;

/// <summary>L'organizzazione della richiesta e il ruolo che ci ha l'utente. Valorizzata da <see cref="OrgAccessFilter"/>.</summary>
public class CurrentOrg
{
    public Guid TenantId { get; private set; }
    public OrgRole Role { get; private set; }

    internal void Set(Guid tenantId, OrgRole role)
    {
        TenantId = tenantId;
        Role = role;
    }
}

/// <summary>Il ruolo minimo che una rotta richiede. Senza, basta essere membri.</summary>
public sealed record RequiredOrgRole(OrgRole Role);

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
/// direbbe che quell'id esiste.</para>
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

        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequiredOrgRole>()?.Role ?? OrgRole.Viewer;
        if (membership.Role < required)
        {
            throw ApiProblem.Forbidden("Il tuo ruolo in questa organizzazione non permette questa operazione.");
        }

        http.RequestServices.GetRequiredService<TenantContext>().Set(orgId);
        http.RequestServices.GetRequiredService<CurrentOrg>().Set(orgId, membership.Role);

        return await next(context);
    }
}

public static class OrgAccessExtensions
{
    public static TBuilder RequireOrgRole<TBuilder>(this TBuilder builder, OrgRole role)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiredOrgRole(role));

    /// <summary>Il gruppo <c>/orgs/{orgId}</c>, con autenticazione e controllo di appartenenza già applicati.</summary>
    public static RouteGroupBuilder MapOrgGroup(this IEndpointRouteBuilder api, string prefix = "") =>
        api.MapGroup("/orgs/{orgId:guid}" + prefix)
            .RequireAuthorization()
            .AddEndpointFilter<OrgAccessFilter>();
}
