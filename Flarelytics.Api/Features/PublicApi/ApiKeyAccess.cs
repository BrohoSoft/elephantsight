using System.Threading.RateLimiting;
using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.PublicApi;

/// <summary>La chiave della richiesta e la sua organizzazione. Valorizzata da <see cref="ApiKeyFilter"/>.</summary>
public class CurrentApiKey
{
    public Guid KeyId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Chi ha creato la chiave: i post che arrivano risultano creati da lui.</summary>
    public Guid CreatedByUserId { get; private set; }

    internal void Set(ApiKey key)
    {
        KeyId = key.Id;
        TenantId = key.TenantId;
        CreatedByUserId = key.CreatedByUserId;
    }
}

/// <summary>
/// La porta delle rotte <c>/public</c>: legge <c>Authorization: Bearer wsk_…</c>,
/// trova la chiave dall'hash e solo allora imposta il tenant.
/// </summary>
/// <remarks>
/// È, con <see cref="Orgs.OrgAccessFilter"/> e il worker, l'unico posto in cui
/// si chiama <see cref="TenantContext.Set"/>. Da qui in poi filtri di EF e RLS
/// vedono solo l'organizzazione della chiave. Una chiave sbagliata o cancellata
/// risponde 401 senza dire quale delle due.
/// </remarks>
public class ApiKeyFilter : IEndpointFilter
{
    public const string RateLimitPolicy = "api-key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var secret = ReadKey(http.Request) ?? throw Unauthorized();

        var db = http.RequestServices.GetRequiredService<FlarelyticsDbContext>();
        var key = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.KeyHash == ApiKey.Hash(secret), http.RequestAborted)
            ?? throw Unauthorized();

        if (key.TouchIfStale(DateTime.UtcNow)) await db.SaveChangesAsync(http.RequestAborted);

        http.RequestServices.GetRequiredService<TenantContext>().Set(key.TenantId);
        http.RequestServices.GetRequiredService<CurrentApiKey>().Set(key);
        return await next(context);
    }

    /// <summary>La chiave dall'header Authorization, se ha la forma di una chiave ElephantSight.</summary>
    public static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer " + ApiKey.Marker, StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
    }

    private static ApiProblem Unauthorized() =>
        new(StatusCodes.Status401Unauthorized, "invalid_api_key", "Chiave API mancante o non valida: mandala come Authorization: Bearer wsk_…");

    /// <summary>
    /// 120 richieste al minuto per chiave: basta per un'automazione, e una
    /// chiave finita nel posto sbagliato non può martellare l'istanza. La
    /// partizione è sull'hash, non sulla chiave in chiaro.
    /// </summary>
    public static RateLimitPartition<string> Partition(HttpContext http) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ReadKey(http.Request) is { } key ? ApiKey.Hash(key) : http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) });
}

public static class ApiKeyAccessExtensions
{
    /// <summary>Il gruppo <c>/public</c>: niente login del pannello, solo la chiave API.</summary>
    public static RouteGroupBuilder MapApiKeyGroup(this IEndpointRouteBuilder api, string prefix) =>
        api.MapGroup("/public" + prefix)
            .AllowAnonymous()
            .AddEndpointFilter<ApiKeyFilter>()
            .RequireRateLimiting(ApiKeyFilter.RateLimitPolicy);
}
