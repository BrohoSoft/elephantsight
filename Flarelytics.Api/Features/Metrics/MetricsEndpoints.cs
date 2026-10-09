using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Metrics;

/// <summary>
/// I numeri della dashboard: di tutta l'organizzazione o di un progetto.
/// Rotta <c>GET /api/v1/orgs/{orgId}/metrics?days=30&amp;projectId=…</c>.
/// </summary>
/// <remarks>
/// <para>Conta solo le app collegate ai progetti: le metriche si calcolano per
/// tutte le app dell'account sviluppatore, ma l'utente ha scelto quali
/// guardare collegandole.</para>
///
/// <para><b>Il periodo finisce all'ultimo giorno con dati</b>, non a oggi: gli
/// store pubblicano con uno o due giorni di ritardo, e un periodo che finisce
/// oggi avrebbe sempre gli ultimi giorni a zero, come se le vendite fossero
/// crollate.</para>
/// </remarks>
public static class MetricsEndpoints
{
    private static readonly int[] AllowedDays = [7, 30, 90, 365];
    private const int TopCountries = 15;

    public static void MapMetrics(this IEndpointRouteBuilder api)
    {
        api.MapOrgGroup().RequireSection(AppSections.Store).MapGet("/metrics", Get);
    }

    private static async Task<IResult> Get(FlarelyticsDbContext db, CurrentOrg org, int? days, Guid? projectId, CancellationToken ct)
    {
        var length = days is { } d && AllowedDays.Contains(d) ? d : 30;
        if (projectId is not null) org.EnsureCanSee(projectId);
        var visible = org.VisibleProjects;

        // Le app dei progetti che il membro vede: chi ne vede solo alcuni ha la dashboard di quelli.
        var linked = await db.Set<ProjectApp>().AsNoTracking()
            .Where(a => projectId == null || a.ProjectId == projectId)
            .Where(a => visible == null || visible.Contains(a.ProjectId))
            .Select(a => new { a.ProjectId, a.Store, a.ExternalAppId, a.CredentialId })
            .ToListAsync(ct);

        if (projectId is not null && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
        {
            throw ApiProblem.NotFound("Progetto");
        }

        var appleIds = linked.Where(a => a.Store == Store.AppStore).Select(a => a.ExternalAppId).Distinct().ToList();
        var googleIds = linked.Where(a => a.Store == Store.GooglePlay).Select(a => a.ExternalAppId).Distinct().ToList();

        var scope = db.Set<DailyAppMetric>().AsNoTracking().Where(m =>
            (m.Store == Store.AppStore && appleIds.Contains(m.AppId)) ||
            (m.Store == Store.GooglePlay && googleIds.Contains(m.AppId)));

        var credentialIds = linked.Select(a => a.CredentialId).Distinct().ToList();
        var lastSync = await db.Set<StoreCredential>()
            .Where(c => credentialIds.Contains(c.Id))
            .MaxAsync(c => c.LastSyncCompletedAtUtc, ct);

        var coverage = StoreCoverage.Current;

        var latest = await scope.MaxAsync(m => (DateOnly?)m.Date, ct);
        if (latest is null)
        {
            return Results.Ok(MetricsResponse.Empty(length, linked.Count > 0, lastSync, coverage));
        }

        var to = latest.Value;
        var from = to.AddDays(-(length - 1));
        var previousFrom = from.AddDays(-length);

        var period = scope.Where(m => m.Date >= from && m.Date <= to);

        var byStore = await period
            .GroupBy(m => m.Store)
            .Select(g => new StoreTotals(g.Key,
                g.Sum(m => m.Downloads), g.Sum(m => m.Redownloads), g.Sum(m => m.Updates), g.Sum(m => m.Uninstalls),
                g.Sum(m => m.InAppPurchases), g.Sum(m => m.Refunds),
                g.Sum(m => m.ProceedsEurMicros) / 1_000_000m, g.Sum(m => m.SalesEurMicros) / 1_000_000m))
            .ToListAsync(ct);

        var previous = await scope
            .Where(m => m.Date >= previousFrom && m.Date < from)
            .GroupBy(m => 1)
            .Select(g => new { Downloads = g.Sum(m => m.Downloads), Proceeds = g.Sum(m => m.ProceedsEurMicros) / 1_000_000m })
            .SingleOrDefaultAsync(ct);

        // Gli ordinamenti stanno prima della Select: EF non sa tradurre un
        // OrderBy sulle proprietà di un record costruito nella proiezione.
        var daily = await period
            .GroupBy(m => new { m.Date, m.Store })
            .OrderBy(g => g.Key.Date)
            .Select(g => new DailyPoint(g.Key.Date, g.Key.Store, g.Sum(m => m.Downloads), g.Sum(m => m.ProceedsEurMicros) / 1_000_000m))
            .ToListAsync(ct);

        var countries = await period
            .GroupBy(m => m.CountryCode)
            .OrderByDescending(g => g.Sum(m => m.Downloads)).ThenByDescending(g => g.Sum(m => m.ProceedsEurMicros))
            .Take(TopCountries)
            .Select(g => new CountryTotals(g.Key, g.Sum(m => m.Downloads), g.Sum(m => m.ProceedsEurMicros) / 1_000_000m))
            .ToListAsync(ct);

        var byApp = await period
            .GroupBy(m => new { m.Store, m.AppId })
            .Select(g => new { g.Key.Store, g.Key.AppId, Downloads = g.Sum(m => m.Downloads), Proceeds = g.Sum(m => m.ProceedsEurMicros) / 1_000_000m })
            .ToListAsync(ct);

        // I progetti si ricompongono in memoria dalle loro app: sono poche.
        var byProject = linked
            .GroupBy(a => a.ProjectId)
            .Select(g => new ProjectTotals(g.Key,
                g.Sum(a => byApp.Where(x => x.Store == a.Store && x.AppId == a.ExternalAppId).Sum(x => x.Downloads)),
                g.Sum(a => byApp.Where(x => x.Store == a.Store && x.AppId == a.ExternalAppId).Sum(x => x.Proceeds))))
            .ToList();

        var hasUnconverted = await period.AnyAsync(m => m.HasUnconvertedAmounts, ct);

        return Results.Ok(new MetricsResponse(
            from, to, length, "EUR", true,
            byStore, previous?.Downloads ?? 0, previous?.Proceeds ?? 0,
            daily, countries, byProject, hasUnconverted, lastSync, coverage));
    }
}

public record StoreTotals(Store Store, int Downloads, int Redownloads, int Updates, int Uninstalls, int InAppPurchases, int Refunds, decimal ProceedsEur, decimal SalesEur);

/// <summary>
/// Quali metriche fornisce ogni store. Dove manca, il frontend mostra un
/// trattino e non uno zero: Apple non comunica le disinstallazioni, Google non
/// distingue i riscaricamenti.
/// </summary>
/// <remarks>
/// I ricavi non sono in elenco per scelta: il prodotto mostra solo download e
/// installazioni. Le colonne dei ricavi esistono ancora nelle metriche Apple
/// (vengono dallo stesso report dei download) ma non si espongono.
/// </remarks>
public record StoreCoverage(Store Store, IReadOnlyList<string> Metrics)
{
    public static readonly IReadOnlyList<StoreCoverage> Current =
    [
        new(Store.AppStore, ["downloads", "redownloads", "updates"]),
        new(Store.GooglePlay, ["downloads", "updates", "uninstalls"])
    ];
}

public record DailyPoint(DateOnly Date, Store Store, int Downloads, decimal ProceedsEur);
public record CountryTotals(string CountryCode, int Downloads, decimal ProceedsEur);
public record ProjectTotals(Guid ProjectId, int Downloads, decimal ProceedsEur);

/// <param name="From">Primo giorno del periodo, compreso.</param>
/// <param name="To">Ultimo giorno con dati, compreso.</param>
/// <param name="HasLinkedApps">False se nessuna app è collegata: il frontend propone di collegarne una invece di mostrare zeri.</param>
/// <param name="PreviousDownloads">Download del periodo di uguale lunghezza subito prima, per la variazione.</param>
/// <param name="HasUnconvertedAmounts">Parte dei ricavi era in valute senza cambio BCE e non è nei totali.</param>
public record MetricsResponse(
    DateOnly? From, DateOnly? To, int Days, string Currency, bool HasLinkedApps,
    IReadOnlyList<StoreTotals> ByStore, int PreviousDownloads, decimal PreviousProceedsEur,
    IReadOnlyList<DailyPoint> Daily, IReadOnlyList<CountryTotals> Countries, IReadOnlyList<ProjectTotals> ByProject,
    bool HasUnconvertedAmounts, DateTime? LastSyncAtUtc, IReadOnlyList<StoreCoverage> Coverage)
{
    public static MetricsResponse Empty(int days, bool hasLinkedApps, DateTime? lastSync, IReadOnlyList<StoreCoverage> coverage) =>
        new(null, null, days, "EUR", hasLinkedApps, [], 0, 0, [], [], [], false, lastSync, coverage);
}
