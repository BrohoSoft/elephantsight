using System.Text.RegularExpressions;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Icons;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Management;
using Flarelytics.Core.Secrets;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Projects;

/// <summary>
/// I progetti di un'organizzazione e le app collegate.
/// Rotte sotto <c>/api/v1/orgs/{orgId}/projects</c>.
/// </summary>
/// <remarks>
/// Nessuna query qui filtra per tenant a mano: lo fanno il filtro di EF e la
/// Row-Level Security, impostati da <see cref="OrgAccessFilter"/>. Un id di
/// progetto di un'altra organizzazione semplicemente non si trova.
/// </remarks>
public static partial class ProjectEndpoints
{
    public static void MapProjects(this IEndpointRouteBuilder api)
    {
        var projects = api.MapOrgGroup("/projects");

        projects.MapGet("", List);
        projects.MapGet("/{projectId:guid}", Get);

        // Le modifiche: almeno admin.
        var write = projects.MapGroup("").RequireOrgRole(OrgRole.Admin);
        write.MapPost("", Create).Validating<ProjectRequest>();
        write.MapPut("/{projectId:guid}", Update).Validating<ProjectRequest>();
        write.MapDelete("/{projectId:guid}", Delete);
        write.MapPut("/{projectId:guid}/apps", LinkApp).Validating<LinkAppRequest>();
        write.MapDelete("/{projectId:guid}/apps/{store}", UnlinkApp);
    }

    private static async Task<IResult> List(FlarelyticsDbContext db, CancellationToken ct)
    {
        var projects = await db.Set<Project>().AsNoTracking()
            .Include(p => p.Apps).ThenInclude(a => a.Credential)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var icons = await IconEndpoints.UrlsAsync(db, projects.SelectMany(p => p.Apps).Select(a => (a.Store, a.ExternalAppId)), ct);
        return Results.Ok(projects.Select(p => ProjectResponse.From(p, icons)));
    }

    private static async Task<IResult> Get(Guid projectId, FlarelyticsDbContext db, CancellationToken ct)
    {
        var project = await LoadAsync(db, projectId, ct);
        var icons = await IconEndpoints.UrlsAsync(db, project.Apps.Select(a => (a.Store, a.ExternalAppId)), ct);
        return Results.Ok(ProjectResponse.From(project, icons));
    }

    /// <summary>Crea un progetto vuoto: le app si collegano dopo.</summary>
    private static async Task<IResult> Create(ProjectRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        await EnsureNameIsFreeAsync(db, req.Name, null, ct);

        var project = Project.Create(org.TenantId, req.Name, req.Description);
        db.Add(project);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/orgs/{org.TenantId}/projects/{project.Id}", ProjectResponse.From(project));
    }

    private static async Task<IResult> Update(Guid projectId, ProjectRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var project = await LoadAsync(db, projectId, ct);
        await EnsureNameIsFreeAsync(db, req.Name, projectId, ct);

        project.Update(req.Name, req.Description);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ProjectResponse.From(project));
    }

    /// <summary>
    /// Cancella il progetto con tutto quello che è suo, anche fuori dal
    /// database: i file di firma cifrati e le build in attesa di caricamento.
    /// </summary>
    private static async Task<IResult> Delete(Guid projectId, FlarelyticsDbContext db, SecretVault vault, UploadStorage uploads, CancellationToken ct)
    {
        var project = await LoadAsync(db, projectId, ct);
        var files = await db.Set<ProjectSecretFile>().Where(f => f.ProjectId == projectId).Select(f => f.Id).ToListAsync(ct);
        var pending = await db.Set<BuildUpload>().Where(u => u.ProjectId == projectId && u.StoragePath != null).Select(u => u.StoragePath).ToListAsync(ct);

        db.Remove(project);
        await db.SaveChangesAsync(ct);

        foreach (var file in files) vault.Delete(project.TenantId, file);
        foreach (var path in pending) uploads.Delete(path);
        return Results.NoContent();
    }

    /// <summary>
    /// Collega un'app al progetto. Lo store lo decide la credenziale: con una
    /// chiave Apple si collega l'app App Store, e quella che c'era prima viene
    /// sostituita.
    /// </summary>
    private static async Task<IResult> LinkApp(Guid projectId, LinkAppRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var project = await LoadAsync(db, projectId, ct);
        var credential = await db.Set<StoreCredential>().SingleOrDefaultAsync(c => c.Id == req.CredentialId, ct)
            ?? throw ApiProblem.NotFound("Credenziale");

        var appId = req.ExternalAppId.Trim();
        var valid = credential.Store switch
        {
            // L'Apple ID dell'app, quello numerico: è l'id che compare nei report.
            Store.AppStore => appId.All(char.IsAsciiDigit),
            Store.GooglePlay => PackageName().IsMatch(appId),
            _ => false
        };

        if (!valid)
        {
            throw ApiProblem.BadRequest("invalid_app_id", credential.Store == Store.AppStore
                ? "Per l'App Store serve l'Apple ID numerico dell'app, non il bundle id."
                : "Per Google Play serve il package name dell'app, per esempio com.azienda.app.");
        }

        project.LinkApp(credential, appId, req.DisplayName);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ProjectResponse.From(project));
    }

    private static async Task<IResult> UnlinkApp(Guid projectId, Store store, FlarelyticsDbContext db, CancellationToken ct)
    {
        var project = await LoadAsync(db, projectId, ct);
        if (!project.UnlinkApp(store)) throw ApiProblem.NotFound("App");

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<Project> LoadAsync(FlarelyticsDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Set<Project>()
            .Include(p => p.Apps).ThenInclude(a => a.Credential)
            .SingleOrDefaultAsync(p => p.Id == projectId, ct)
        ?? throw ApiProblem.NotFound("Progetto");

    private static async Task EnsureNameIsFreeAsync(FlarelyticsDbContext db, string name, Guid? except, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (await db.Set<Project>().AnyAsync(p => p.Name == trimmed && p.Id != except, ct))
        {
            throw ApiProblem.Conflict("project_name_taken", "Esiste già un progetto con questo nome.");
        }
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$")]
    private static partial Regex PackageName();
}

public record ProjectRequest(string Name, string? Description);

/// <param name="ExternalAppId">Apple ID numerico per l'App Store, package name per Google Play.</param>
/// <param name="DisplayName">Il nome da mostrare; di solito quello che restituisce l'elenco delle app della credenziale.</param>
public record LinkAppRequest(Guid CredentialId, string ExternalAppId, string? DisplayName);

/// <param name="IconUrl">L'icona dello store, quando il worker l'ha già scaricata.</param>
public record ProjectAppResponse(Guid Id, Store Store, string ExternalAppId, string? DisplayName, Guid CredentialId, string CredentialLabel, string? IconUrl);

/// <param name="IconUrl">L'icona del progetto: quella dell'App Store se c'è, altrimenti quella di Google Play.</param>
public record ProjectResponse(Guid Id, string Name, string? Description, IReadOnlyList<ProjectAppResponse> Apps, DateTime CreatedAtUtc, string? IconUrl)
{
    public static ProjectResponse From(Project p, IReadOnlyDictionary<(Store, string), string>? icons = null)
    {
        var apps = p.Apps.OrderBy(a => a.Store)
            .Select(a => new ProjectAppResponse(a.Id, a.Store, a.ExternalAppId, a.DisplayName, a.CredentialId, a.Credential.Label,
                icons?.GetValueOrDefault((a.Store, a.ExternalAppId))))
            .ToList();

        return new ProjectResponse(p.Id, p.Name, p.Description, apps, p.CreatedAtUtc, apps.Select(a => a.IconUrl).FirstOrDefault(u => u is not null));
    }
}

public class ProjectRequestValidator : AbstractValidator<ProjectRequest>
{
    public ProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public class LinkAppRequestValidator : AbstractValidator<LinkAppRequest>
{
    public LinkAppRequestValidator()
    {
        RuleFor(x => x.CredentialId).NotEmpty();
        RuleFor(x => x.ExternalAppId).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).MaximumLength(200);
    }
}
