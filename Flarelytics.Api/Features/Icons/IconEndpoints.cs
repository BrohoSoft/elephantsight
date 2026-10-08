using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Icons;

/// <summary>
/// Le icone delle app: <c>GET /api/v1/icons/{id}</c>, senza autenticazione.
/// </summary>
/// <remarks>
/// Anonima perché un <c>&lt;img&gt;</c> non manda il bearer token. Non rivela
/// niente: l'immagine è quella pubblica dello store, e l'id è un Guid casuale
/// che conosce solo chi vede il progetto (vedi <see cref="AppIcon"/>).
/// </remarks>
public static class IconEndpoints
{
    public static void MapIcons(this IEndpointRouteBuilder api)
    {
        api.MapGet("/icons/{iconId:guid}", Get).AllowAnonymous();
    }

    private static async Task<IResult> Get(Guid iconId, FlarelyticsDbContext db, IconStorage storage, HttpResponse response, CancellationToken ct)
    {
        var icon = await db.Set<AppIcon>().AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == iconId && i.Status == AppIconStatus.Stored, ct);

        if (icon?.RelativePath is null) return Results.NotFound();

        var path = storage.PathFor(icon.RelativePath);
        if (!File.Exists(path)) return Results.NotFound();

        // Un giorno di cache: l'icona cambia di rado, e quando cambia il
        // worker la riscarica entro una settimana.
        response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(path, icon.ContentType ?? "image/png");
    }

    /// <summary>Gli indirizzi delle icone già scaricate per le app indicate.</summary>
    public static async Task<Dictionary<(Store, string), string>> UrlsAsync(
        FlarelyticsDbContext db, IEnumerable<(Store Store, string AppId)> apps, CancellationToken ct)
    {
        var appIds = apps.Select(a => a.AppId).Distinct().ToList();
        if (appIds.Count == 0) return [];

        var icons = await db.Set<AppIcon>().AsNoTracking()
            .Where(i => appIds.Contains(i.AppId) && i.Status == AppIconStatus.Stored)
            .Select(i => new { i.Store, i.AppId, i.Id })
            .ToListAsync(ct);

        return icons.ToDictionary(i => (i.Store, i.AppId), i => $"/api/v1/icons/{i.Id}");
    }
}
