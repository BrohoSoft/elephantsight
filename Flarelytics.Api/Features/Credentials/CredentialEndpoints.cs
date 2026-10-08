using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Credentials;

/// <summary>
/// Le chiavi degli store di un'organizzazione.
/// Rotte sotto <c>/api/v1/orgs/{orgId}/credentials</c>.
/// </summary>
/// <remarks>
/// <para><b>Il segreto entra e non esce più.</b> Si carica una volta, si
/// verifica con lo store, si cifra su disco; nessuna rotta lo restituisce, né
/// in chiaro né cifrato. Per cambiarlo si carica una credenziale nuova e si
/// cancella la vecchia.</para>
///
/// <para>Il file arriva come testo dentro il JSON, non come upload multipart:
/// un .p8 e un JSON di service account sono pochi KB di testo, e il frontend li
/// legge con <c>FileReader.readAsText</c>. Così la richiesta passa dalla
/// stessa validazione di tutte le altre.</para>
/// </remarks>
public static partial class CredentialEndpoints
{
    public static void MapCredentials(this IEndpointRouteBuilder api)
    {
        var credentials = api.MapOrgGroup("/credentials");

        credentials.MapGet("", List);

        var admin = credentials.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapGet("/{credentialId:guid}/apps", ListApps);

        var write = admin.MapGroup("");
        write.MapPost("/app-store", CreateAppStore).Validating<AppStoreCredentialRequest>();
        write.MapPost("/google-play", CreateGooglePlay).Validating<GooglePlayCredentialRequest>();
        write.MapPost("/{credentialId:guid}/verify", Verify);
        write.MapPost("/{credentialId:guid}/sync", RequestSync);
        write.MapPatch("/{credentialId:guid}", Rename).Validating<RenameCredentialRequest>();
        write.MapDelete("/{credentialId:guid}", Delete);
    }

    private static async Task<IResult> List(FlarelyticsDbContext db, CancellationToken ct)
    {
        var credentials = await db.Set<StoreCredential>().AsNoTracking()
            .OrderBy(c => c.Store).ThenBy(c => c.Label)
            .ToListAsync(ct);

        // Quanti giorni sono già stati scaricati, per mostrare l'avanzamento
        // del primo recupero dello storico.
        var imported = await db.Set<ReportFile>()
            .GroupBy(f => f.CredentialId)
            .Select(g => new { CredentialId = g.Key, Days = g.Count(), Latest = g.Where(f => f.Status == ReportFileStatus.Stored).Max(f => (DateOnly?)f.ReportDate) })
            .ToDictionaryAsync(x => x.CredentialId, ct);

        return Results.Ok(credentials.Select(c =>
            imported.TryGetValue(c.Id, out var i)
                ? CredentialResponse.From(c) with { DaysImported = i.Days, LatestReportDate = i.Latest }
                : CredentialResponse.From(c)));
    }

    /// <summary>Chiede al worker di sincronizzare la chiave al prossimo giro, senza aspettare il suo turno.</summary>
    private static async Task<IResult> RequestSync(Guid credentialId, FlarelyticsDbContext db, CancellationToken ct)
    {
        var credential = await LoadAsync(db, credentialId, ct);
        credential.RequestSync(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(CredentialResponse.From(credential));
    }

    private static async Task<IResult> CreateAppStore(
        AppStoreCredentialRequest req, CurrentOrg org, FlarelyticsDbContext db, StoreGateways gateways,
        SecretVault vault, CancellationToken ct)
    {
        var key = AppleKey.Parse(req.KeyId, req.IssuerId, req.VendorNumber, req.PrivateKey);
        var credential = StoreCredential.ForAppStore(
            org.TenantId, req.Label, key.Fingerprint(), key.KeyId, key.IssuerId, key.VendorNumber);

        return await SaveAsync(credential, key.PrivateKeyPem, db, gateways, vault, ct);
    }

    private static async Task<IResult> CreateGooglePlay(
        GooglePlayCredentialRequest req, CurrentOrg org, FlarelyticsDbContext db, StoreGateways gateways,
        SecretVault vault, CancellationToken ct)
    {
        var account = GoogleServiceAccount.Parse(req.ServiceAccountJson);
        var credential = StoreCredential.ForGooglePlay(
            org.TenantId, req.Label, account.Fingerprint(), account.ClientEmail!, NormalizeBucket(req.ReportsBucket));

        return await SaveAsync(credential, req.ServiceAccountJson, db, gateways, vault, ct);
    }

    /// <summary>
    /// Verifica con lo store, poi cifra e salva.
    /// </summary>
    /// <remarks>
    /// Una chiave che lo store rifiuta non si salva: è quasi sempre un file
    /// sbagliato, e tenerla vorrebbe dire un progetto che non riceverà mai dati
    /// senza che nessuno capisca perché. Una chiave autentica ma con permessi
    /// incompleti invece si salva, con l'avviso: i permessi di Google Play
    /// possono arrivare ore dopo l'invito.
    /// </remarks>
    private static async Task<IResult> SaveAsync(
        StoreCredential credential, string secret, FlarelyticsDbContext db, StoreGateways gateways,
        SecretVault vault, CancellationToken ct)
    {
        if (await db.Set<StoreCredential>().AnyAsync(c => c.Fingerprint == credential.Fingerprint, ct))
        {
            throw ApiProblem.Conflict("credential_exists", "Questa chiave è già stata caricata in questa organizzazione.");
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var result = await gateways.For(credential.Store).VerifyAsync(credential, bytes, ct);
            RejectIfUnusable(result);

            credential.RecordVerification(StatusOf(result), result.Message, DateTime.UtcNow);
            credential.SetKeyVersion(await vault.WriteAsync(credential.TenantId, credential.Id, bytes, ct));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        db.Add(credential);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Senza la riga il file è irraggiungibile: non resta sul disco.
            vault.Delete(credential.TenantId, credential.Id);
            throw;
        }

        return Results.Created($"/api/v1/orgs/{credential.TenantId}/credentials/{credential.Id}", CredentialResponse.From(credential));
    }

    /// <summary>Rifà la verifica: dopo aver sistemato i permessi sullo store, o per sapere se una chiave vale ancora.</summary>
    private static async Task<IResult> Verify(
        Guid credentialId, FlarelyticsDbContext db, StoreGateways gateways, CredentialSecrets secrets, CancellationToken ct)
    {
        var credential = await LoadAsync(db, credentialId, ct);
        var result = await secrets.UseAsync(credential,
            secret => gateways.For(credential.Store).VerifyAsync(credential, secret, ct), ct);

        // Qui una chiave rifiutata resta salvata, segnata come non valida: la
        // usano dei progetti, e cancellarla di nascosto sarebbe peggio.
        if (result.Outcome is not VerificationOutcome.Unreachable)
        {
            credential.RecordVerification(StatusOf(result), result.Message, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(CredentialResponse.From(credential) with { LastCheckMessage = result.Message });
    }

    /// <summary>Le app che la chiave vede: il frontend le propone quando si collega un'app a un progetto.</summary>
    private static async Task<IResult> ListApps(
        Guid credentialId, FlarelyticsDbContext db, StoreGateways gateways, CredentialSecrets secrets, CancellationToken ct)
    {
        var credential = await LoadAsync(db, credentialId, ct);
        var apps = await secrets.UseAsync(credential,
            secret => gateways.For(credential.Store).ListAppsAsync(credential, secret, ct), ct);

        return Results.Ok(apps);
    }

    private static async Task<IResult> Rename(
        Guid credentialId, RenameCredentialRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var credential = await LoadAsync(db, credentialId, ct);
        credential.Rename(req.Label);
        await db.SaveChangesAsync(ct);
        return Results.Ok(CredentialResponse.From(credential));
    }

    /// <summary>
    /// Cancella la riga e il file cifrato. Una chiave usata da qualche progetto
    /// non si cancella: si risponde 409 con i progetti da scollegare prima.
    /// </summary>
    private static async Task<IResult> Delete(
        Guid credentialId, FlarelyticsDbContext db, SecretVault vault, Core.Reports.ReportStorage reports, CancellationToken ct)
    {
        var credential = await LoadAsync(db, credentialId, ct);

        var usedBy = await db.Set<ProjectApp>()
            .Where(a => a.CredentialId == credentialId)
            .Join(db.Set<Project>(), a => a.ProjectId, p => p.Id, (a, p) => p.Name)
            .ToListAsync(ct);

        if (usedBy.Count > 0)
        {
            throw ApiProblem.Conflict("credential_in_use",
                $"La chiave è usata da: {string.Join(", ", usedBy)}. Scollega prima le app da questi progetti.");
        }

        db.Remove(credential);
        await db.SaveChangesAsync(ct);

        // Dopo il salvataggio: se fallisse prima, la riga resterebbe e il file
        // no, e la credenziale sarebbe rotta senza essere cancellata.
        vault.Delete(credential.TenantId, credential.Id);

        // Anche i report grezzi scaricati con questa chiave. Le metriche già
        // calcolate restano: sono legate all'app, e un'altra chiave dello
        // stesso account continuerà ad aggiornarle.
        reports.DeleteCredential(credential.TenantId, credential.Id);

        return Results.NoContent();
    }

    private static async Task<StoreCredential> LoadAsync(FlarelyticsDbContext db, Guid credentialId, CancellationToken ct) =>
        await db.Set<StoreCredential>().SingleOrDefaultAsync(c => c.Id == credentialId, ct)
        ?? throw ApiProblem.NotFound("Credenziale");

    private static void RejectIfUnusable(VerificationResult result)
    {
        switch (result.Outcome)
        {
            case VerificationOutcome.Rejected:
                throw new ApiProblem(StatusCodes.Status422UnprocessableEntity, "credential_rejected", result.Message!);
            case VerificationOutcome.Unreachable:
                throw new ApiProblem(StatusCodes.Status503ServiceUnavailable, "store_unreachable", result.Message!);
        }
    }

    private static CredentialStatus StatusOf(VerificationResult result) => result.Outcome switch
    {
        VerificationOutcome.Ok => CredentialStatus.Valid,
        VerificationOutcome.Limited => CredentialStatus.Limited,
        _ => CredentialStatus.Invalid
    };

    /// <summary>
    /// Accetta il bucket com'è scritto in Play Console, cioè come URI
    /// (<c>gs://pubsite_prod_rev_123/</c>), o solo il nome.
    /// </summary>
    internal static string? NormalizeBucket(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return null;

        var name = bucket.Trim();
        if (name.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        return name.Split('/', 2)[0];
    }

    [GeneratedRegex("^pubsite_prod_[a-z0-9_]+$")]
    internal static partial Regex ReportsBucket();
}

/// <param name="Label">Un nome per riconoscerla, per esempio "Team Acme".</param>
/// <param name="PrivateKey">Il contenuto del file .p8, così com'è.</param>
/// <param name="VendorNumber">Serve per i report di vendita. Si può aggiungere dopo, ma senza le vendite non arrivano.</param>
public record AppStoreCredentialRequest(string Label, string KeyId, string IssuerId, string? VendorNumber, string PrivateKey);

/// <param name="ServiceAccountJson">Il contenuto del file JSON della chiave, così com'è.</param>
/// <param name="ReportsBucket">Il bucket dei report in blocco, come URI <c>gs://…</c> o solo il nome.</param>
public record GooglePlayCredentialRequest(string Label, string ServiceAccountJson, string? ReportsBucket);

public record RenameCredentialRequest(string Label);

/// <summary>Una credenziale vista da fuori: metadati e stato, mai il segreto.</summary>
public record CredentialResponse(
    Guid Id, Store Store, string Label,
    string? KeyId, string? IssuerId, string? VendorNumber,
    string? ClientEmail, string? ReportsBucket,
    CredentialStatus Status, string? StatusMessage, DateTime? LastVerifiedAtUtc, DateTime CreatedAtUtc,
    bool SyncRequested, DateTime? LastSyncCompletedAtUtc, string? LastSyncError,
    string? LastCheckMessage = null, int DaysImported = 0, DateOnly? LatestReportDate = null)
{
    public static CredentialResponse From(StoreCredential c) => new(
        c.Id, c.Store, c.Label,
        c.AppleKeyId, c.AppleIssuerId, c.AppleVendorNumber,
        c.GoogleClientEmail, c.GoogleReportsBucket,
        c.Status, c.StatusMessage, c.LastVerifiedAtUtc, c.CreatedAtUtc,
        c.SyncRequestedAtUtc is not null, c.LastSyncCompletedAtUtc, c.LastSyncError);
}

/// <summary>
/// I file sono pochi KB: 16 KB di tetto bastano con ampio margine e
/// tengono fuori chi prova a mandare megabyte.
/// </summary>
internal static class SecretLimits
{
    public const int MaxLength = 16 * 1024;
}

public class AppStoreCredentialRequestValidator : AbstractValidator<AppStoreCredentialRequest>
{
    public AppStoreCredentialRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.KeyId).NotEmpty().MaximumLength(20);
        RuleFor(x => x.IssuerId).NotEmpty().MaximumLength(36);
        RuleFor(x => x.VendorNumber).MaximumLength(20);
        RuleFor(x => x.PrivateKey).NotEmpty().MaximumLength(SecretLimits.MaxLength);
    }
}

public class GooglePlayCredentialRequestValidator : AbstractValidator<GooglePlayCredentialRequest>
{
    public GooglePlayCredentialRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ServiceAccountJson).NotEmpty().MaximumLength(SecretLimits.MaxLength);
        RuleFor(x => x.ReportsBucket)
            .Must(b => CredentialEndpoints.NormalizeBucket(b) is not { } name || CredentialEndpoints.ReportsBucket().IsMatch(name))
            .WithMessage("Il bucket dei report ha la forma gs://pubsite_prod_…: copialo da Play Console, Scarica report.");
    }
}

public class RenameCredentialRequestValidator : AbstractValidator<RenameCredentialRequest>
{
    public RenameCredentialRequestValidator() => RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
}
