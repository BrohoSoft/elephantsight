using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Flarelytics.Api.Auth;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// La cassaforte dei file di firma di un progetto: keystore, key.properties,
/// certificati, profili, file di configurazione.
/// Rotte sotto <c>/orgs/{orgId}/projects/{projectId}/files</c>.
/// </summary>
/// <remarks>
/// Cifrati come le chiavi degli store. Si possono riscaricare (servono per
/// firmare le build fuori da qui), ma solo da un admin e solo confermando di
/// nuovo password e, se attiva, il codice della 2FA: un access token rubato o
/// un computer lasciato aperto non bastano a portarsi via un keystore.
/// </remarks>
public static class SecretFileEndpoints
{
    public const long MaxBytes = 10 * 1024 * 1024;

    public static void MapSecretFiles(this IEndpointRouteBuilder api)
    {
        var files = api.MapOrgGroup("/projects/{projectId:guid}/files").RequireSection(AppSections.Store);
        files.MapGet("", List);

        var admin = files.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapPost("", Upload).DisableAntiforgery();
        admin.MapPatch("/{fileId:guid}", Update).Validating<UpdateSecretFileRequest>();
        admin.MapDelete("/{fileId:guid}", Delete);
        admin.MapPost("/{fileId:guid}/download", Download).Validating<DownloadSecretFileRequest>().RequireRateLimiting(Features.Auth.AuthEndpoints.RateLimitPolicy);
    }

    private static async Task<IResult> List(Guid projectId, FlarelyticsDbContext db, CancellationToken ct)
    {
        await ProjectAppsLoader.LoadAsync(db, projectId, ct); // 404 se il progetto non è dell'organizzazione

        var files = await db.Set<ProjectSecretFile>().AsNoTracking().Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.Platform).ThenBy(f => f.Kind).ThenBy(f => f.Name).ToListAsync(ct);
        return Results.Ok(files.Select(SecretFileResponse.From));
    }

    /// <summary>
    /// Un file (<c>file</c>) o un testo incollato (<c>text</c>), più
    /// <c>platform</c>, <c>kind</c>, <c>name</c> e <c>notes</c>.
    /// </summary>
    private static async Task<IResult> Upload(
        Guid projectId, HttpRequest request, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db, SecretVault vault, CancellationToken ct)
    {
        await ProjectAppsLoader.LoadAsync(db, projectId, ct);
        var form = await request.ReadFormAsync(ct);

        if (!Enum.TryParse<SecretPlatform>(form["platform"], out var platform)) throw ApiProblem.BadRequest("platform", "Indica la piattaforma.");
        if (!Enum.TryParse<SecretKind>(form["kind"], out var kind)) throw ApiProblem.BadRequest("kind", "Indica il tipo di file.");
        var name = form["name"].ToString().Trim();
        if (name.Length is 0 or > 200) throw ApiProblem.BadRequest("name", "Dai un nome al file, al massimo 200 caratteri.");

        byte[] content;
        string fileName;
        if (form.Files.GetFile("file") is { } file)
        {
            if (file.Length is 0 or > MaxBytes) throw ApiProblem.BadRequest("file_size", "Il file deve pesare meno di 10 MB.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
            fileName = Path.GetFileName(file.FileName);
        }
        else if (form["text"].ToString() is { Length: > 0 } text)
        {
            content = Encoding.UTF8.GetBytes(text);
            fileName = form["fileName"].ToString() is { Length: > 0 } f ? Path.GetFileName(f) : name + ".txt";
        }
        else throw ApiProblem.BadRequest("file_missing", "Carica un file o incolla il contenuto.");

        try
        {
            var entry = ProjectSecretFile.Create(org.TenantId, projectId, platform, kind, name, fileName, content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)), form["notes"], principal.UserId());
            entry.SetKeyVersion(await vault.WriteAsync(org.TenantId, entry.Id, content, ct));

            db.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                vault.Delete(org.TenantId, entry.Id);
                throw;
            }

            return Results.Created($"/api/v1/orgs/{org.TenantId}/projects/{projectId}/files/{entry.Id}", SecretFileResponse.From(entry));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<IResult> Update(Guid projectId, Guid fileId, UpdateSecretFileRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var entry = await LoadAsync(db, projectId, fileId, ct);
        entry.Update(req.Name, req.Notes);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SecretFileResponse.From(entry));
    }

    private static async Task<IResult> Delete(Guid projectId, Guid fileId, FlarelyticsDbContext db, SecretVault vault, CancellationToken ct)
    {
        var entry = await LoadAsync(db, projectId, fileId, ct);
        db.Remove(entry);
        await db.SaveChangesAsync(ct);
        vault.Delete(entry.TenantId, entry.Id);
        return Results.NoContent();
    }

    /// <summary>
    /// Restituisce il file in chiaro, dopo aver riverificato chi lo chiede:
    /// password sempre, codice della 2FA se l'utente l'ha attiva.
    /// </summary>
    private static async Task<IResult> Download(
        Guid projectId, Guid fileId, DownloadSecretFileRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db,
        SecretVault vault, TwoFactorService twoFactor, CancellationToken ct)
    {
        var entry = await LoadAsync(db, projectId, fileId, ct);
        var userId = principal.UserId();
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);

        if (!user.VerifyPassword(req.Password)) throw ApiProblem.BadRequest("invalid_password", "La password non è corretta.");
        if (user.IsTwoFactorEnabled && (req.Code is null || !twoFactor.VerifyCode(user, req.Code, DateTime.UtcNow)))
            throw ApiProblem.BadRequest("invalid_code", "Serve il codice della verifica in due passaggi.");

        var content = await vault.ReadAsync(entry.TenantId, entry.Id, ct);
        entry.RecordDownload(userId, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return Results.File(content, "application/octet-stream", entry.FileName);
    }

    private static async Task<ProjectSecretFile> LoadAsync(FlarelyticsDbContext db, Guid projectId, Guid fileId, CancellationToken ct) =>
        await db.Set<ProjectSecretFile>().SingleOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct)
        ?? throw ApiProblem.NotFound("File");
}

public record SecretFileResponse(Guid Id, SecretPlatform Platform, SecretKind Kind, string Name, string FileName, long SizeBytes,
    string Sha256, string? Notes, DateTime CreatedAtUtc, DateTime? LastDownloadedAtUtc)
{
    public static SecretFileResponse From(ProjectSecretFile f) =>
        new(f.Id, f.Platform, f.Kind, f.Name, f.FileName, f.SizeBytes, f.Sha256, f.Notes, f.CreatedAtUtc, f.LastDownloadedAtUtc);
}

public record UpdateSecretFileRequest(string Name, string? Notes);

/// <param name="Code">Il codice dell'app di autenticazione, se l'utente ha la 2FA attiva.</param>
public record DownloadSecretFileRequest(string Password, string? Code);

public class UpdateSecretFileRequestValidator : AbstractValidator<UpdateSecretFileRequest>
{
    public UpdateSecretFileRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class DownloadSecretFileRequestValidator : AbstractValidator<DownloadSecretFileRequest>
{
    public DownloadSecretFileRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Code).MaximumLength(20);
    }
}
