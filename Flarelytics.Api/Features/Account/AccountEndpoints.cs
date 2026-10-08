using System.Security.Claims;
using Flarelytics.Api.Auth;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Account;

/// <summary>
/// Il proprio account: nome, password, verifica in due passaggi.
/// Rotte sotto <c>/api/v1/me</c>.
/// </summary>
/// <remarks>
/// Le operazioni che indeboliscono o cambiano la protezione dell'account
/// (password, attivazione e disattivazione della 2FA) chiedono di nuovo la
/// password o un codice: un access token rubato, o un computer lasciato
/// aperto, non devono bastare.
/// </remarks>
public static class AccountEndpoints
{
    public static void MapAccount(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me").RequireAuthorization();

        me.MapPatch("", UpdateProfile).Validating<UpdateProfileRequest>();
        me.MapPost("/password", ChangePassword).Validating<ChangePasswordRequest>();

        me.MapPost("/2fa/setup", BeginTwoFactor).Validating<PasswordRequest>();
        me.MapPost("/2fa/enable", EnableTwoFactor).Validating<CodeRequest>();
        me.MapPost("/2fa/disable", DisableTwoFactor).Validating<DisableTwoFactorRequest>();
        me.MapPost("/2fa/recovery-codes", RegenerateRecoveryCodes).Validating<CodeRequest>();
    }

    private static async Task<IResult> UpdateProfile(UpdateProfileRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);
        user.Rename(req.FullName);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Cambia la password, chiude tutte le sessioni e ne apre subito una nuova
    /// per chi l'ha cambiata: gli altri dispositivi escono, questo resta dentro.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, TokenService tokens,
        RefreshTokenService sessions, RefreshCookie cookie, HttpResponse response, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);
        RequirePassword(user, req.CurrentPassword);

        var now = DateTime.UtcNow;
        user.ChangePassword(req.NewPassword);
        await sessions.RevokeAllAsync(user.Id, now, ct);
        var refresh = sessions.Issue(user, now);
        await db.SaveChangesAsync(ct);

        cookie.Write(response, refresh, now);
        return Results.Ok(AuthEndpoints.Session(tokens, user, now));
    }

    /// <summary>
    /// Primo passo: un seme nuovo, da mostrare come QR code. La 2FA non è
    /// ancora attiva finché l'utente non conferma con un codice dell'app: chi
    /// chiude la pagina a metà non si ritrova chiuso fuori.
    /// </summary>
    private static async Task<IResult> BeginTwoFactor(
        PasswordRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, TwoFactorService twoFactor, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);
        RequirePassword(user, req.Password);

        if (user.IsTwoFactorEnabled)
        {
            throw ApiProblem.Conflict("two_factor_enabled", "La verifica in due passaggi è già attiva.");
        }

        var (secret, uri) = twoFactor.BeginSetup(user);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new TwoFactorSetupResponse(secret, uri));
    }

    /// <summary>Secondo passo: il primo codice dell'app la attiva, e si ricevono i codici di recupero.</summary>
    private static async Task<IResult> EnableTwoFactor(
        CodeRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, TwoFactorService twoFactor, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);

        if (user.IsTwoFactorEnabled) throw ApiProblem.Conflict("two_factor_enabled", "La verifica in due passaggi è già attiva.");
        if (user.TotpSecretProtected is null) throw ApiProblem.BadRequest("two_factor_not_started", "Prima avvia la configurazione.");

        var now = DateTime.UtcNow;
        if (!twoFactor.VerifyCode(user, req.Code, now)) throw InvalidCode();

        var (codes, hashes) = TwoFactorService.NewRecoveryCodes();
        user.EnableTwoFactor(now, user.TotpLastUsedStep!.Value, hashes);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new RecoveryCodesResponse(codes));
    }

    /// <summary>Disattiva la 2FA. Vuole password e codice: chi ha solo uno dei due non deve poterla togliere.</summary>
    private static async Task<IResult> DisableTwoFactor(
        DisableTwoFactorRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, TwoFactorService twoFactor, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);
        RequirePassword(user, req.Password);

        if (!user.IsTwoFactorEnabled) return Results.NoContent();
        if (!twoFactor.VerifyCodeOrRecovery(user, req.Code, DateTime.UtcNow)) throw InvalidCode();

        user.DisableTwoFactor();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Codici di recupero nuovi: quelli vecchi smettono di valere.</summary>
    private static async Task<IResult> RegenerateRecoveryCodes(
        CodeRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, TwoFactorService twoFactor, CancellationToken ct)
    {
        var user = await LoadAsync(db, principal, ct);
        if (!user.IsTwoFactorEnabled) throw ApiProblem.BadRequest("two_factor_disabled", "La verifica in due passaggi non è attiva.");
        if (!twoFactor.VerifyCode(user, req.Code, DateTime.UtcNow)) throw InvalidCode();

        var (codes, hashes) = TwoFactorService.NewRecoveryCodes();
        user.ReplaceRecoveryCodes(hashes);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new RecoveryCodesResponse(codes));
    }

    private static async Task<User> LoadAsync(FlarelyticsDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.UserId();
        return await db.Set<User>().SingleAsync(u => u.Id == id, ct);
    }

    private static void RequirePassword(User user, string password)
    {
        if (!user.VerifyPassword(password))
        {
            throw ApiProblem.BadRequest("invalid_password", "La password non è corretta.");
        }
    }

    private static ApiProblem InvalidCode() => ApiProblem.BadRequest("invalid_code", "Il codice non è corretto.");
}

public record UpdateProfileRequest(string FullName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record PasswordRequest(string Password);
public record CodeRequest(string Code);
public record DisableTwoFactorRequest(string Password, string Code);

/// <param name="Secret">Il seme in base32, per chi lo inserisce a mano invece di inquadrare il QR.</param>
/// <param name="OtpAuthUri">Il contenuto del QR code.</param>
public record TwoFactorSetupResponse(string Secret, string OtpAuthUri);

/// <param name="Codes">Si vedono solo ora: l'utente li deve salvare.</param>
public record RecoveryCodesResponse(IReadOnlyList<string> Codes);

public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator() => RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}

public class PasswordRequestValidator : AbstractValidator<PasswordRequest>
{
    public PasswordRequestValidator() => RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
}

public class CodeRequestValidator : AbstractValidator<CodeRequest>
{
    public CodeRequestValidator() => RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
}

public class DisableTwoFactorRequestValidator : AbstractValidator<DisableTwoFactorRequest>
{
    public DisableTwoFactorRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
    }
}
