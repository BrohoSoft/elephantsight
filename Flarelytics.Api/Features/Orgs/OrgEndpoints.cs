using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Flarelytics.Api.Features.Orgs;

/// <summary>Profilo e organizzazioni.</summary>
public static class OrgEndpoints
{
    public static void MapOrgs(this IEndpointRouteBuilder api)
    {
        api.MapGet("/me", Me).RequireAuthorization();

        api.MapPost("/orgs", CreateOrg).RequireAuthorization().Validating<CreateOrgRequest>();

        var org = api.MapOrgGroup();
        org.MapGet("", GetOrg);
        org.MapPatch("", RenameOrg).RequireOrgRole(OrgRole.Admin).Validating<RenameOrgRequest>();
    }

    /// <summary>L'utente e le organizzazioni a cui appartiene: è la prima chiamata del frontend dopo l'accesso.</summary>
    private static async Task<IResult> Me(ClaimsPrincipal principal, FlarelyticsDbContext db, CancellationToken ct)
    {
        var userId = principal.UserId();
        var user = await db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == userId, ct);

        var orgs = await db.Set<Membership>().AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Tenant.Name)
            .Select(m => new OrgSummary(m.TenantId, m.Tenant.Name, m.Role))
            .ToListAsync(ct);

        return Results.Ok(new MeResponse(user.Id, user.Email, user.FullName, user.IsTwoFactorEnabled, orgs));
    }

    /// <summary>Un'altra organizzazione: per tenere separate app di clienti o di progetti diversi.</summary>
    private static async Task<IResult> CreateOrg(CreateOrgRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, CancellationToken ct)
    {
        var org = Tenant.Create(req.Name);
        db.AddRange(org, Membership.Create(org.Id, principal.UserId(), OrgRole.Owner));
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/orgs/{org.Id}", new CreateOrgResponse(org.Id));
    }

    private static async Task<IResult> GetOrg(CurrentOrg current, FlarelyticsDbContext db, CancellationToken ct)
    {
        var org = await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == current.TenantId, ct);
        return Results.Ok(new OrgSummary(org.Id, org.Name, current.Role));
    }

    private static async Task<IResult> RenameOrg(RenameOrgRequest req, CurrentOrg current, FlarelyticsDbContext db, CancellationToken ct)
    {
        var org = await db.Set<Tenant>().SingleAsync(t => t.Id == current.TenantId, ct);
        org.Rename(req.Name);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new OrgSummary(org.Id, org.Name, current.Role));
    }
}

public record OrgSummary(Guid Id, string Name, OrgRole Role);
public record MeResponse(Guid Id, string Email, string FullName, bool TwoFactorEnabled, IReadOnlyList<OrgSummary> Organizations);

public record CreateOrgRequest(string Name);
public record CreateOrgResponse(Guid Id);
public record RenameOrgRequest(string Name);

public class CreateOrgRequestValidator : AbstractValidator<CreateOrgRequest>
{
    public CreateOrgRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}

public class RenameOrgRequestValidator : AbstractValidator<RenameOrgRequest>
{
    public RenameOrgRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}
