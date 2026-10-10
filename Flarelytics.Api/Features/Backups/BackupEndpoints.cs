using System.Security.Cryptography;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Instance;
using Flarelytics.Core.Backups;
using Flarelytics.Core.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Features.Backups;

/// <summary>
/// I backup dell'istanza nel pannello: stato, storico, "fai un backup ora",
/// scaricare e cancellare i file. Solo per gli amministratori dell'istanza, e
/// solo se la funzione c'è (<c>BACKUPS_ENABLED</c>): altrimenti queste rotte non
/// vengono nemmeno registrate.
/// </summary>
/// <remarks>
/// Programmazione e password stanno nelle impostazioni dell'istanza (gruppo
/// <c>backup</c>). Il ripristino non c'è: si fa da riga di comando, a istanza
/// ferma (<see cref="BackupCommand"/>).
/// </remarks>
public static class BackupEndpoints
{
    private const string DownloadPurpose = "backup-download";
    private static readonly TimeSpan DownloadValidity = TimeSpan.FromMinutes(5);

    public static void MapBackups(this IEndpointRouteBuilder api)
    {
        var backups = api.MapGroup("/instance/backups").RequireAuthorization().AddEndpointFilter<InstanceAdminFilter>();
        backups.MapGet("", Status);
        backups.MapPost("/run", Run);
        backups.MapPost("/files/{name}/link", DownloadLink);
        backups.MapDelete("/files/{name}", Delete);

        // Anonima con un token a scadenza: un file da centinaia di MB si scarica
        // con un link normale del browser, non passando dalla memoria del pannello.
        api.MapGet("/backups/download/{token}", Download).AllowAnonymous();
    }

    private static async Task<IResult> Status(BackupService service, BackupRunner runner, IOptionsMonitor<BackupOptions> options, FlarelyticsDbContext db,
        CancellationToken ct)
    {
        var o = options.CurrentValue;
        var runs = await db.Set<BackupRun>().AsNoTracking().OrderByDescending(r => r.StartedAtUtc).Take(20).ToListAsync(ct);
        return Results.Ok(new BackupStatus(o.Active, o.Time, o.TimeZone, o.EveryDays, o.Keep, o.HasPassword, service.Directory, runner.Running,
            o.Active && o.HasPassword ? BackupSchedule.NextSlotUtc(o, DateTime.UtcNow) : null,
            service.Files(),
            runs.Select(r => new BackupRunItem(r.Id, r.StartedAtUtc, r.FinishedAtUtc, r.Manual, r.FileName, r.SizeBytes, r.Error)).ToList()));
    }

    private static IResult Run(BackupRunner runner, IOptionsMonitor<BackupOptions> options)
    {
        if (!options.CurrentValue.HasPassword)
            throw ApiProblem.BadRequest("backup_password_missing", "Imposta prima la password dei backup: senza, non si possono cifrare.");
        if (!runner.StartManual()) throw ApiProblem.Conflict("backup_running", "C'è già un backup in corso.");
        return Results.Accepted();
    }

    private static IResult DownloadLink(string name, BackupService service, IDataProtectionProvider protection)
    {
        if (service.PathOf(name) is null) throw ApiProblem.NotFound("Backup");
        var token = protection.CreateProtector(DownloadPurpose).ToTimeLimitedDataProtector().Protect(name, DownloadValidity);
        return Results.Ok(new { url = $"/api/v1/backups/download/{token}" });
    }

    private static IResult Download(string token, BackupService service, IDataProtectionProvider protection)
    {
        string name;
        try
        {
            name = protection.CreateProtector(DownloadPurpose).ToTimeLimitedDataProtector().Unprotect(token);
        }
        catch (CryptographicException)
        {
            return Results.NotFound();
        }
        return service.PathOf(name) is { } path
            ? Results.File(path, "application/octet-stream", name, enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static IResult Delete(string name, BackupService service)
    {
        if (service.PathOf(name) is null) throw ApiProblem.NotFound("Backup");
        service.Delete(name);
        return Results.NoContent();
    }
}

/// <param name="NextRunUtc">Il prossimo backup programmato (null se spenti o senza password).</param>
public record BackupStatus(bool Active, string Time, string TimeZone, int EveryDays, int Keep, bool PasswordSet, string Directory, bool Running,
    DateTime? NextRunUtc, IReadOnlyList<BackupFile> Files, IReadOnlyList<BackupRunItem> Runs);

public record BackupRunItem(Guid Id, DateTime StartedAtUtc, DateTime? FinishedAtUtc, bool Manual, string? FileName, long? SizeBytes, string? Error);
