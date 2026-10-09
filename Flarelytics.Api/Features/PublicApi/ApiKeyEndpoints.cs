using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.PublicApi;

/// <summary>
/// Le chiavi API dell'organizzazione, dal pannello. Rotte sotto <c>/orgs/{orgId}/api-keys</c>, solo admin.
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> non è del tenant (si legge prima di sapere di chi è),
/// quindi qui ogni query filtra per <see cref="CurrentOrg.TenantId"/> a mano.
/// </remarks>
public static class ApiKeyEndpoints
{
    public const int MaxKeysPerOrg = 50;

    public static void MapApiKeys(this IEndpointRouteBuilder api)
    {
        var keys = api.MapOrgGroup("/api-keys").RequireOrgRole(OrgRole.Admin).RequireFullAccess();
        keys.MapGet("", List);
        keys.MapPost("", Create).Validating<CreateApiKeyRequest>();
        keys.MapDelete("/{keyId:guid}", Delete);
    }

    private static async Task<IResult> List(CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var keys = await db.Set<ApiKey>().AsNoTracking().Where(k => k.TenantId == org.TenantId).OrderBy(k => k.CreatedAtUtc).ToListAsync(ct);
        var userIds = keys.Select(k => k.CreatedByUserId).Distinct().ToList();
        var names = await db.Set<User>().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return Results.Ok(keys.Select(k => ApiKeyResponse.From(k, names.GetValueOrDefault(k.CreatedByUserId))));
    }

    /// <summary>La chiave in chiaro esce solo qui, una volta.</summary>
    private static async Task<IResult> Create(CreateApiKeyRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        if (await db.Set<ApiKey>().CountAsync(k => k.TenantId == org.TenantId, ct) >= MaxKeysPerOrg)
            throw ApiProblem.Conflict("too_many_keys", $"Al massimo {MaxKeysPerOrg} chiavi per organizzazione: cancella quelle che non usi.");

        var (key, secret) = ApiKey.Create(org.TenantId, req.Name, principal.UserId());
        db.Add(key);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CreatedApiKeyResponse(ApiKeyResponse.From(key, null), secret));
    }

    /// <summary>Revoca: da subito la chiave non apre più niente. I post già arrivati restano in coda.</summary>
    private static async Task<IResult> Delete(Guid keyId, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var key = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.Id == keyId && k.TenantId == org.TenantId, ct) ?? throw ApiProblem.NotFound("Chiave");
        db.Remove(key);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public record ApiKeyResponse(Guid Id, string Name, string Prefix, string? CreatedBy, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc)
{
    public static ApiKeyResponse From(ApiKey k, string? createdBy) => new(k.Id, k.Name, k.Prefix, createdBy, k.CreatedAtUtc, k.LastUsedAtUtc);
}

/// <param name="Secret">La chiave da copiare: non si potrà più rileggere.</param>
public record CreatedApiKeyResponse(ApiKeyResponse Key, string Secret);

public record CreateApiKeyRequest(string Name);

public class CreateApiKeyRequestValidator : AbstractValidator<CreateApiKeyRequest>
{
    public CreateApiKeyRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
}
