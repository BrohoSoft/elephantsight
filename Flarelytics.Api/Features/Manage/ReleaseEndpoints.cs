using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Management;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// Versioni e build di un progetto, App Store e Google Play affiancati.
/// <c>GET /api/v1/orgs/{orgId}/projects/{projectId}/releases</c>.
/// </summary>
/// <remarks>
/// Si leggono dallo store a ogni richiesta, uno store alla volta: sono dati
/// che cambiano di ora in ora (una build finisce l'elaborazione, una versione
/// passa la revisione), e una copia locale sarebbe quasi sempre vecchia.
/// </remarks>
public static class ReleaseEndpoints
{
    public static void MapReleases(this IEndpointRouteBuilder api)
    {
        api.MapOrgGroup("/projects/{projectId:guid}").MapGet("/releases", Get);
    }

    private static async Task<IResult> Get(Guid projectId, FlarelyticsDbContext db, ReleasesService releases, CancellationToken ct)
    {
        var apps = await ProjectAppsLoader.LoadAsync(db, projectId, ct);
        var result = new List<StoreReleases>();

        foreach (var app in apps)
        {
            result.Add(await releases.GetAsync(app.Credential, app.ExternalAppId, ct));
        }

        return Results.Ok(result);
    }
}
