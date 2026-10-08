using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Sync;

/// <summary>
/// Scarica le icone delle app collegate ai progetti del tenant corrente che
/// non ce l'hanno ancora, o che l'hanno da più di una settimana.
/// </summary>
public class IconRefresher(FlarelyticsDbContext db, IAppIconSource source, IconStorage storage, ILogger<IconRefresher> log)
{
    /// <summary>Un giro non scarica più di tante icone: il servizio di Apple tollera circa 20 richieste al minuto.</summary>
    private const int MaxPerRun = 10;

    public async Task<int> RefreshAsync(DateTime nowUtc, CancellationToken ct)
    {
        var linked = await db.Set<ProjectApp>().AsNoTracking()
            .Select(a => new { a.Store, a.ExternalAppId })
            .Distinct()
            .ToListAsync(ct);

        var refreshed = 0;
        foreach (var app in linked)
        {
            if (refreshed >= MaxPerRun) break;

            var icon = await db.Set<AppIcon>().SingleOrDefaultAsync(i => i.Store == app.Store && i.AppId == app.ExternalAppId, ct);
            if (icon is not null && icon.NextAttemptAtUtc > nowUtc) continue;

            if (icon is null)
            {
                icon = AppIcon.Create(app.Store, app.ExternalAppId);
                db.Add(icon);
            }

            // I paesi dove l'app ha più download: lì è sicuramente pubblicata.
            var countries = await db.Set<DailyAppMetric>()
                .Where(m => m.Store == app.Store && m.AppId == app.ExternalAppId && m.CountryCode != "ZZ")
                .GroupBy(m => m.CountryCode)
                .OrderByDescending(g => g.Sum(m => m.Downloads))
                .Select(g => g.Key)
                .Take(3)
                .ToListAsync(ct);

            try
            {
                var image = await source.FetchAsync(app.Store, app.ExternalAppId, countries, ct);
                if (image is null) icon.MarkUnavailable(AppIconStatus.NotFound, nowUtc);
                else icon.MarkStored(await storage.WriteAsync(icon, image, ct), image.ContentType, nowUtc);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                log.LogWarning("Icona di {Store} {App} non disponibile: {Message}", app.Store, app.ExternalAppId, e.Message);
                icon.MarkUnavailable(AppIconStatus.Failed, nowUtc);
            }

            await db.SaveChangesAsync(ct);
            refreshed++;
        }

        return refreshed;
    }
}
