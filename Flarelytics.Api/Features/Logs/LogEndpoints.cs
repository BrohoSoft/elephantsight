using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Logs;

/// <summary>
/// I log salvati a database (vedi <see cref="StoredLogProvider"/>), dal più
/// recente. Rotta <c>/orgs/{orgId}/logs</c>, per chi gestisce l'organizzazione.
/// </summary>
/// <remarks>
/// <see cref="LogEntry"/> non è del tenant: il filtro va messo a mano. Si
/// vedono i messaggi di questa organizzazione e, solo a un owner, quelli di
/// sistema (di nessuna organizzazione: avvio, migration, errori generali),
/// perché in un'installazione self-hosted l'owner è chi la gestisce.
/// </remarks>
public static class LogEndpoints
{
    private const int PageSize = 100;

    public static void MapLogs(this IEndpointRouteBuilder api)
    {
        api.MapOrgGroup("/logs").RequireOrgRole(OrgRole.Admin).RequireFullAccess().MapGet("", List);
    }

    /// <param name="level">Il minimo: Information (tutto), Warning, Error.</param>
    /// <param name="area">Social, Store o Sistema.</param>
    /// <param name="q">Testo da cercare nel messaggio (un id di post, il nome di una rete…).</param>
    /// <param name="before">Per la pagina successiva: l'id più basso della pagina prima.</param>
    private static async Task<IResult> List(CurrentOrg org, FlarelyticsDbContext db, LogLevel? level, string? area, string? q, long? before, CancellationToken ct)
    {
        var tenant = org.TenantId;
        var system = org.Role == OrgRole.Owner;
        var query = db.Set<LogEntry>().AsNoTracking().Where(e => e.TenantId == tenant || (system && e.TenantId == null));

        if (level is { } min) query = query.Where(e => e.Level >= min);
        if (!string.IsNullOrWhiteSpace(area)) query = query.Where(e => e.Area == area);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(e => EF.Functions.ILike(e.Message, pattern) || EF.Functions.ILike(e.Category, pattern));
        }
        if (before is { } id) query = query.Where(e => e.Id < id);

        var entries = await query.OrderByDescending(e => e.Id).Take(PageSize + 1).ToListAsync(ct);
        return Results.Ok(new LogPage(
            entries.Take(PageSize).Select(e => new LogItem(e.Id, e.TimestampUtc, e.Level.ToString(), e.Area, e.Category, e.Message, e.Exception, e.TenantId == null)).ToList(),
            entries.Count > PageSize));
    }
}

/// <param name="System">Un messaggio di sistema, di nessuna organizzazione.</param>
public record LogItem(long Id, DateTime TimestampUtc, string Level, string Area, string Category, string Message, string? Exception, bool System);

/// <param name="HasMore">Ci sono messaggi più vecchi: si chiedono con <c>before</c> = l'ultimo id.</param>
public record LogPage(IReadOnlyList<LogItem> Items, bool HasMore);
