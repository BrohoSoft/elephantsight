using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// Le app di un progetto con le loro chiavi: il punto di partenza di tutte le
/// operazioni di gestione (versioni, recensioni, pagina dello store, build).
/// </summary>
/// <remarks>Le query passano dal filtro del tenant: un progetto di un'altra organizzazione non si trova.</remarks>
public static class ProjectAppsLoader
{
    public static async Task<List<ProjectApp>> LoadAsync(FlarelyticsDbContext db, Guid projectId, CancellationToken ct)
    {
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct)) throw ApiProblem.NotFound("Progetto");

        return await db.Set<ProjectApp>()
            .Include(a => a.Credential)
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.Store)
            .ToListAsync(ct);
    }

    /// <summary>L'app di uno store, o 404 se il progetto non ne ha una.</summary>
    public static async Task<ProjectApp> LoadAsync(FlarelyticsDbContext db, Guid projectId, Store store, CancellationToken ct) =>
        (await LoadAsync(db, projectId, ct)).SingleOrDefault(a => a.Store == store)
        ?? throw ApiProblem.NotFound(store == Store.AppStore ? "App App Store del progetto" : "App Google Play del progetto");
}
