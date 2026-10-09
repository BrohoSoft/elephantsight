using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Management;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// Caricamento delle build dal pannello: .aab verso Google Play, .ipa verso
/// App Store Connect. Rotte sotto <c>/orgs/{orgId}/projects/{projectId}/builds</c>.
/// </summary>
/// <remarks>
/// La richiesta salva il file e lo mette in coda; lo manda allo store il
/// <see cref="BuildUploadWorker"/>. Il pannello segue lo stato con la lista.
/// </remarks>
public static class BuildEndpoints
{
    /// <summary>4 GB: Apple accetta .ipa fino a 4 GB, Google bundle fino a 4 GB scaricabili.</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ReleaseStatuses = ["completed", "draft", "inProgress"];

    public static void MapBuilds(this IEndpointRouteBuilder api)
    {
        var builds = api.MapOrgGroup("/projects/{projectId:guid}/builds").RequireSection(AppSections.Store);
        builds.MapGet("/uploads", List);
        builds.MapPost("/uploads", Upload).RequireOrgRole(OrgRole.Admin).DisableAntiforgery();
    }

    private static async Task<IResult> List(Guid projectId, FlarelyticsDbContext db, CancellationToken ct)
    {
        await ProjectAppsLoader.LoadAsync(db, projectId, ct);
        var uploads = await db.Set<BuildUpload>().AsNoTracking().Where(u => u.ProjectId == projectId)
            .OrderByDescending(u => u.CreatedAtUtc).Take(30).ToListAsync(ct);
        return Results.Ok(uploads.Select(BuildUploadResponse.From));
    }

    /// <summary>
    /// Multipart letto in streaming: il file va dritto su disco senza passare
    /// dalla memoria (può essere di centinaia di MB). Campi: <c>store</c>; per
    /// Google <c>track</c>, <c>releaseStatus</c>, <c>rolloutPercent</c>,
    /// <c>releaseName</c>, <c>releaseNotesLanguage</c>, <c>releaseNotes</c>;
    /// e il file. Per Apple versione e build si leggono dall'.ipa.
    /// </summary>
    private static async Task<IResult> Upload(
        Guid projectId, HttpRequest request, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        UploadStorage storage, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBytes;

        var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value
            ?? throw ApiProblem.BadRequest("multipart", "Serve un caricamento multipart.");
        var reader = new MultipartReader(boundary, request.Body);

        var fields = new Dictionary<string, string>();
        var uploadId = Guid.CreateVersion7();
        string? path = null, fileName = null;
        long size = 0;

        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null) continue;

                if (disposition.IsFileDisposition())
                {
                    fileName = Path.GetFileName(disposition.FileName.Value ?? disposition.FileNameStar.Value ?? "build");
                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    if (extension is not (".aab" or ".ipa")) throw ApiProblem.BadRequest("file_type", "Carica un .aab per Google Play o un .ipa per App Store.");

                    path = storage.PathFor(org.TenantId, uploadId, extension);
                    await using var target = File.Create(path);
                    await section.Body.CopyToAsync(target, ct);
                    size = target.Length;
                }
                else
                {
                    using var text = new StreamReader(section.Body);
                    fields[disposition.Name.Value ?? ""] = (await text.ReadToEndAsync(ct)).Trim();
                }
            }

            if (path is null || fileName is null || size == 0) throw ApiProblem.BadRequest("file_missing", "Manca il file della build.");
            if (!Enum.TryParse<Store>(fields.GetValueOrDefault("store"), out var store)) throw ApiProblem.BadRequest("store", "Indica lo store.");

            var app = await ProjectAppsLoader.LoadAsync(db, projectId, store, ct);
            var userId = principal.UserId();
            BuildUpload upload;

            if (store == Store.AppStore)
            {
                if (!fileName.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase)) throw ApiProblem.BadRequest("file_type", "Per l'App Store serve un .ipa.");

                IpaInfo info;
                try
                {
                    await using var ipa = File.OpenRead(path);
                    info = IpaReader.Read(ipa);
                }
                catch (Exception e) when (e is FormatException or InvalidDataException)
                {
                    throw ApiProblem.BadRequest("ipa_invalid", $"Non riesco a leggere l'.ipa: {e.Message}");
                }

                upload = BuildUpload.ForApple(org.TenantId, projectId, app, fileName, size, path, info.Version, info.BuildNumber, userId);
            }
            else
            {
                if (!fileName.EndsWith(".aab", StringComparison.OrdinalIgnoreCase)) throw ApiProblem.BadRequest("file_type", "Per Google Play serve un .aab.");

                var track = fields.GetValueOrDefault("track") is { Length: > 0 } t ? t : "internal";
                var status = fields.GetValueOrDefault("releaseStatus") is { Length: > 0 } s ? s : "completed";
                if (!ReleaseStatuses.Contains(status)) throw ApiProblem.BadRequest("release_status", "Stato della release sconosciuto.");

                double? rollout = null;
                if (status == "inProgress")
                {
                    if (!double.TryParse(fields.GetValueOrDefault("rolloutPercent"), System.Globalization.CultureInfo.InvariantCulture, out var r) || r is <= 0 or >= 100)
                        throw ApiProblem.BadRequest("rollout", "Per un rilascio graduale indica una percentuale fra 0 e 100, esclusi.");
                    rollout = r;
                }

                var notes = fields.GetValueOrDefault("releaseNotes");
                if (notes is { Length: > 500 }) throw ApiProblem.BadRequest("release_notes", "Le note di rilascio di Google Play hanno al massimo 500 caratteri.");

                upload = BuildUpload.ForGoogle(org.TenantId, projectId, app, fileName, size, path, track, status, rollout,
                    fields.GetValueOrDefault("releaseName"), fields.GetValueOrDefault("releaseNotesLanguage"), notes, userId);
            }

            db.Add(upload);
            await db.SaveChangesAsync(ct);
            path = null; // ora è del worker
            return Results.Accepted($"/api/v1/orgs/{org.TenantId}/projects/{projectId}/builds/uploads", BuildUploadResponse.From(upload));
        }
        finally
        {
            // Se qualcosa è andato storto prima della coda, il file non resta sul disco.
            storage.Delete(path);
        }
    }
}

public record BuildUploadResponse(Guid Id, Store Store, string FileName, long SizeBytes, string? Version, string? BuildNumber,
    string? Track, string? ReleaseStatus, double? RolloutPercent, BuildUploadStatus Status, string? Message, DateTime CreatedAtUtc, DateTime? FinishedAtUtc)
{
    public static BuildUploadResponse From(BuildUpload u) => new(u.Id, u.Store, u.FileName, u.SizeBytes, u.Version, u.BuildNumber,
        u.Track, u.ReleaseStatus, u.RolloutPercent, u.Status, u.Message, u.CreatedAtUtc, u.FinishedAtUtc);
}
