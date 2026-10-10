using Flarelytics.Api.Auth;
using Flarelytics.Api.Common;
using Flarelytics.Api.Email;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Features.Setup;

/// <summary>
/// L'installazione: lo stato dell'istanza e la creazione del primo utente.
/// </summary>
/// <remarks>
/// <para>Flarelytics non ha registrazione libera. Al primo avvio, finché non
/// esiste nessun utente, il pannello mostra l'installer: chi apre per primo
/// l'indirizzo crea l'account amministratore e la prima organizzazione. Da lì
/// in poi si entra solo per invito.</para>
///
/// <para><b>Chi arriva per primo vince</b>, quindi l'istanza va avviata e
/// configurata subito, prima di esporla. Due installer lanciati nello stesso
/// istante non creano due amministratori: il controllo "nessun utente" e
/// l'inserimento avvengono sotto un lock del database.</para>
/// </remarks>
public static class SetupEndpoints
{
    /// <summary>Chiave arbitraria ma fissa del lock: identifica "l'installazione" fra tutti i lock di Postgres.</summary>
    private const long SetupLockKey = 0x466C617265; // "Flare"

    public static void MapSetup(this IEndpointRouteBuilder api)
    {
        api.MapGet("/instance", Instance).AllowAnonymous();
        api.MapPost("/setup", Setup).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy).Validating<SetupRequest>();
    }

    /// <summary>Quello che il pannello deve sapere prima ancora del login.</summary>
    private static async Task<IResult> Instance(FlarelyticsDbContext db, AccountEmails emails, IOptionsMonitor<SocialOptions> social, IConfiguration configuration,
        Flarelytics.Core.Social.Media.SocialMediaStore media, CancellationToken ct) =>
        Results.Ok(new InstanceInfo(
            SetupRequired: !await db.Set<User>().AnyAsync(ct),
            EmailEnabled: emails.Enabled,
            MetaEnabled: social.CurrentValue.Meta.Enabled,
            MetaRedirectUri: Social.SocialAccountEndpoints.MetaRedirectUri(social),
            InstagramEnabled: social.CurrentValue.Instagram.Enabled,
            InstagramRedirectUri: Social.SocialAccountEndpoints.InstagramRedirectUri(social),
            TikTokEnabled: social.CurrentValue.TikTok.Enabled,
            TikTokRedirectUri: Social.SocialAccountEndpoints.TikTokRedirectUri(social),
            ThreadsEnabled: social.CurrentValue.Threads.Enabled,
            ThreadsRedirectUri: Social.SocialAccountEndpoints.ThreadsRedirectUri(social),
            LegalOwner: NullIfEmpty(configuration["Legal:Owner"]),
            LegalContactEmail: NullIfEmpty(configuration["Legal:ContactEmail"]),
            Version: typeof(SetupEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            MediaStorage: media.Status.ToString().ToLowerInvariant(),
            MediaStorageForced: media.RemoteForced,
            MediaStorageLocked: media.Locked,
            BackupsEnabled: configuration.GetValue("Backups:Enabled", true)));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Crea l'amministratore (owner della prima organizzazione) e apre subito
    /// la sessione. Risponde 409 se l'istanza è già installata.
    /// </summary>
    private static async Task<IResult> Setup(
        SetupRequest req, FlarelyticsDbContext db, TokenService tokens, RefreshTokenService sessions,
        RefreshCookie cookie, HttpResponse response, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [SetupLockKey], ct);

        if (await db.Set<User>().AnyAsync(ct))
        {
            throw ApiProblem.Conflict("setup_done", "ElephantSight è già installato: accedi con il tuo account.");
        }

        var user = User.Create(req.Email, req.Password, req.FullName);
        user.ConfirmEmail(now);
        // Chi installa amministra l'istanza.
        user.SetInstanceAdmin(true);
        var org = Tenant.Create(req.OrganizationName);
        db.AddRange(user, org, Membership.Create(org.Id, user.Id, OrgRole.Owner));

        user.RegisterLogin(now);
        var refresh = sessions.Issue(user, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        cookie.Write(response, refresh, now);
        return Results.Ok(AuthEndpoints.Session(tokens, user, now));
    }
}

/// <param name="SetupRequired">Nessun utente ancora: il pannello mostra l'installer.</param>
/// <param name="EmailEnabled">Senza SMTP gli inviti si mandano copiando il link, e il recupero password non c'è.</param>
/// <param name="MetaEnabled">C'è un'app Meta configurata: si possono collegare Instagram e le Pagine Facebook.</param>
/// <param name="MetaRedirectUri">L'indirizzo da registrare nell'app Meta come URI di reindirizzamento OAuth.</param>
/// <param name="InstagramEnabled">C'è un'app Instagram (Instagram Login): si collegano account Instagram senza Pagina Facebook.</param>
/// <param name="InstagramRedirectUri">L'indirizzo da registrare nelle impostazioni di Business login di Instagram.</param>
/// <param name="TikTokRedirectUri">L'indirizzo da registrare nell'app TikTok (Login Kit → Redirect URI).</param>
/// <param name="ThreadsRedirectUri">L'indirizzo da registrare nel caso d'uso Threads dell'app Meta (Redirect Callback URLs).</param>
/// <param name="LegalOwner">Chi gestisce l'installazione, per le pagine /privacy e /terms (<c>Legal:Owner</c>).</param>
/// <param name="LegalContactEmail">L'email per le richieste sui dati (<c>Legal:ContactEmail</c>).</param>
/// <param name="MediaStorage">Dove vanno immagini e video dei post: <c>local</c>, <c>remote</c> (Bunny) o <c>unavailable</c> (niente caricamenti).</param>
/// <param name="MediaStorageLocked">Bunny si configura solo dall'ambiente (<c>MEDIA_STORAGE_LOCKED</c>): la sezione non c'è nel pannello.</param>
/// <param name="BackupsEnabled">La funzione dei backup c'è (<c>BACKUPS_ENABLED</c>): senza, il pannello non mostra niente dei backup.</param>
/// <param name="MediaStorageForced">La modalità remota è imposta dall'ambiente (<c>MEDIA_STORAGE=remote</c>): il disco non si usa.</param>
public record InstanceInfo(bool SetupRequired, bool EmailEnabled, bool MetaEnabled, string MetaRedirectUri, bool InstagramEnabled, string InstagramRedirectUri,
    bool TikTokEnabled, string TikTokRedirectUri, bool ThreadsEnabled, string ThreadsRedirectUri, string? LegalOwner, string? LegalContactEmail, string Version,
    string MediaStorage, bool MediaStorageForced, bool MediaStorageLocked, bool BackupsEnabled);

public record SetupRequest(string Email, string Password, string FullName, string OrganizationName);

public class SetupRequestValidator : AbstractValidator<SetupRequest>
{
    public SetupRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OrganizationName).NotEmpty().MaximumLength(100);
    }
}
