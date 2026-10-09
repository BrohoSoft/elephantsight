using Flarelytics.Api.Features.Logs;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Api.Features.Social;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Logging;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Overview;

/// <summary>
/// Quello che si vuole vedere appena si apre il pannello, in una chiamata
/// sola: <c>/orgs/{orgId}/overview</c>, o con <c>projectId</c> per la
/// panoramica di un progetto.
/// </summary>
/// <remarks>
/// Ogni parte segue l'accesso del membro: il Social solo a chi ha la sezione
/// Social (e solo dei progetti che vede), i log solo a chi gestisce
/// l'organizzazione. Download e versioni li chiede il pannello alle rotte
/// dello Store, che hanno già i loro controlli.
/// </remarks>
public static class OverviewEndpoints
{
    private const int UpcomingCount = 5;
    private const int LogCount = 10;

    public static void MapOverview(this IEndpointRouteBuilder api) => api.MapOrgGroup().MapGet("/overview", Get);

    private static async Task<IResult> Get(Guid? projectId, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        if (projectId is not null) org.EnsureCanSee(projectId);
        var visible = org.VisibleProjects;
        var projects = await db.Set<Project>().CountAsync(p => visible == null || visible.Contains(p.Id), ct);

        var social = org.Has(AppSections.Social) ? await SocialAsync(projectId, org, db, ct) : null;

        List<LogItem>? logs = null;
        if (projectId is null && org.Role >= OrgRole.Admin && org.IsFull)
        {
            var tenant = org.TenantId;
            var system = org.Role == OrgRole.Owner;
            logs = (await db.Set<LogEntry>().AsNoTracking()
                    .Where(e => e.TenantId == tenant || (system && e.TenantId == null))
                    .OrderByDescending(e => e.Id).Take(LogCount).ToListAsync(ct))
                .Select(e => new LogItem(e.Id, e.TimestampUtc, e.Level.ToString(), e.Area, e.Category, e.Message, e.Exception, e.TenantId == null))
                .ToList();
        }

        return Results.Ok(new OverviewResponse(projects, social, logs));
    }

    private static async Task<SocialOverview> SocialAsync(Guid? projectId, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var posts = SocialPostEndpoints.Visible(db, org).AsNoTracking();
        var recurring = SocialRecurringEndpoints.Visible(db, org).AsNoTracking();
        if (projectId is { } pid)
        {
            posts = posts.Where(p => p.ProjectId == pid);
            recurring = recurring.Where(r => r.ProjectId == pid);
        }

        // Programmati: non bozze, non in coda, con almeno un account ancora da fare.
        var scheduled = posts.Where(p => !p.IsDraft && !p.IsInbox && !p.IsImported && p.Targets.Any(t => t.Status == SocialTargetStatus.Pending));
        var weekAgo = now.AddDays(-7);

        var next = await scheduled.Where(p => p.ScheduledAtUtc >= now.AddMinutes(-15)).Include(p => p.Targets)
            .OrderBy(p => p.ScheduledAtUtc).Take(UpcomingCount).ToListAsync(ct);
        var activeRecurring = await recurring.Where(r => !r.IsPaused && r.NextOccurrenceUtc != null).ToListAsync(ct);
        var accounts = await SocialAccountEndpoints.VisibleAccounts(db, org).AsNoTracking().Select(a => new { a.Id, a.Network }).ToListAsync(ct);

        // I prossimi post, comprese le uscite dei post ricorrenti (che diventano post solo all'ora giusta).
        var upcoming = next.Select(p => new UpcomingItem(p.ScheduledAtUtc, p.Text, p.Targets.Select(t => t.Network).Distinct().ToList(), p.Id, null, p.ProjectId))
            .Concat(activeRecurring.SelectMany(r => r.Rule.Between(now, now.AddDays(60), UpcomingCount)
                .Select(at => new UpcomingItem(at, r.Text, accounts.Where(a => r.AccountIds.Contains(a.Id)).Select(a => a.Network).Distinct().ToList(), null, r.Id, r.ProjectId))))
            .OrderBy(u => u.AtUtc).Take(UpcomingCount).ToList();

        return new SocialOverview(
            await scheduled.CountAsync(ct),
            await posts.CountAsync(p => p.IsInbox, ct),
            activeRecurring.Count,
            await posts.CountAsync(p => p.ScheduledAtUtc >= weekAgo && p.Targets.Any(t => t.Status == SocialTargetStatus.Failed), ct),
            upcoming);
    }
}

/// <param name="Social">Null se il membro non ha la sezione Social.</param>
/// <param name="Logs">Gli ultimi messaggi, solo per chi gestisce l'organizzazione (e non nella panoramica di un progetto).</param>
public record OverviewResponse(int Projects, SocialOverview? Social, IReadOnlyList<LogItem>? Logs);

/// <param name="FailedLastWeek">Post di questa settimana non usciti su almeno un account.</param>
public record SocialOverview(int Scheduled, int Inbox, int RecurringActive, int FailedLastWeek, IReadOnlyList<UpcomingItem> Upcoming);

/// <summary>Un post in arrivo: programmato (<paramref name="PostId"/>) o un'uscita di un post ricorrente (<paramref name="RecurringPostId"/>).</summary>
public record UpcomingItem(DateTime AtUtc, string Text, IReadOnlyList<SocialNetwork> Networks, Guid? PostId, Guid? RecurringPostId, Guid? ProjectId);
