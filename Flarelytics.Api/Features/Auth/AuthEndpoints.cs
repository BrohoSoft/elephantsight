using Flarelytics.Api.Auth;
using Flarelytics.Api.Common;
using Flarelytics.Api.Email;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Billing;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Auth;

/// <summary>
/// Registrazione, conferma dell'email, accesso, sessione e recupero password.
/// Rotte sotto <c>/api/v1/auth</c>.
/// </summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public static void MapAuth(this IEndpointRouteBuilder api)
    {
        // Tutte anonime e tutte con un tetto per IP: sono aperte a chiunque, e
        // senza tetto sarebbero un banco di prova per indovinare password e un
        // modo per riempire di email la casella di qualcun altro.
        var auth = api.MapGroup("/auth").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/register", Register).Validating<RegisterRequest>();
        auth.MapPost("/confirm-email", ConfirmEmail).Validating<TokenRequest>();
        auth.MapPost("/resend-confirmation", ResendConfirmation).Validating<EmailRequest>();
        auth.MapPost("/login", Login).Validating<LoginRequest>();
        auth.MapPost("/login/2fa", LoginSecondFactor).Validating<SecondFactorRequest>();
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout);
        auth.MapPost("/forgot-password", ForgotPassword).Validating<EmailRequest>();
        auth.MapPost("/reset-password", ResetPassword).Validating<ResetPasswordRequest>();
    }

    /// <summary>
    /// Crea l'account. Senza invito crea anche l'organizzazione (di cui
    /// l'utente è owner) e il suo abbonamento; con un invito lo fa entrare in
    /// quella che l'ha invitato.
    /// </summary>
    /// <remarks>
    /// Senza invito l'account non può entrare finché l'email non è confermata.
    /// Con l'invito invece è già confermata: il link è arrivato a
    /// quell'indirizzo, e averlo in mano ne prova il possesso.
    ///
    /// Con i pagamenti in sordina il piano risulta già attivo; con un provider
    /// vero la risposta porterebbe un <c>checkoutUrl</c>.
    /// </remarks>
    private static async Task<IResult> Register(
        RegisterRequest req, FlarelyticsDbContext db, TenantContext tenant, IBillingProvider billing,
        AccountEmails emails, CancellationToken ct)
    {
        var email = User.NormalizeEmail(req.Email);
        if (await db.Set<User>().AnyAsync(u => u.Email == email, ct))
        {
            throw ApiProblem.Conflict("email_taken", "Esiste già un account con questa email.");
        }

        var now = DateTime.UtcNow;
        var user = User.Create(email, req.Password, req.FullName);

        if (req.InvitationToken is { } invitationToken)
        {
            var invitation = await Invitations.FindPendingAsync(db, invitationToken, now, ct);
            if (invitation.Email != email)
            {
                throw ApiProblem.Forbidden("L'invito è per un altro indirizzo email.");
            }

            user.ConfirmEmail(now);
            invitation.Accept(now);
            db.AddRange(user, Membership.Create(invitation.TenantId, user.Id, invitation.Role));
            await db.SaveChangesAsync(ct);

            return Results.Created("/api/v1/me", new RegisterResponse(user.Id, invitation.TenantId, null, EmailConfirmationRequired: false));
        }

        var plan = Plans.Find(req.Plan ?? Plans.Starter.Code)!;
        var org = Tenant.Create(req.OrganizationName!);

        // Prima di salvare: l'abbonamento è una riga del tenant, e la Row-Level
        // Security la accetta solo se la connessione dice di essere quel tenant.
        tenant.Set(org.Id);
        var checkout = await billing.StartAsync(org.Id, plan, ct);

        var token = SecureToken.Create();
        db.AddRange(user, org, Membership.Create(org.Id, user.Id, OrgRole.Owner), checkout.Subscription,
            EmailToken.Issue(user.Id, EmailTokenPurpose.ConfirmEmail, token, now, ConfirmationLifetime));
        await db.SaveChangesAsync(ct);

        await emails.SendConfirmationAsync(user.Email, user.FullName, token, ct);

        return Results.Created("/api/v1/me", new RegisterResponse(user.Id, org.Id, checkout.CheckoutUrl, EmailConfirmationRequired: true));
    }

    private static async Task<IResult> ConfirmEmail(TokenRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = await FindUsableTokenAsync(db, req.Token, EmailTokenPurpose.ConfirmEmail, now, ct);
        if (token is null) throw ApiProblem.BadRequest("invalid_token", "Il link non è valido o è scaduto.");

        var user = await db.Set<User>().SingleAsync(u => u.Id == token.UserId, ct);
        user.ConfirmEmail(now);
        token.MarkUsed(now);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Sempre 202, anche se l'email non esiste o è già confermata: la risposta
    /// non deve dire a un estraneo quali indirizzi sono registrati.
    /// </summary>
    private static async Task<IResult> ResendConfirmation(
        EmailRequest req, FlarelyticsDbContext db, AccountEmails emails, CancellationToken ct)
    {
        var email = User.NormalizeEmail(req.Email);
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct);

        if (user is { IsEmailConfirmed: false })
        {
            var token = await ReplaceTokenAsync(db, user, EmailTokenPurpose.ConfirmEmail, ConfirmationLifetime, ct);
            await emails.SendConfirmationAsync(user.Email, user.FullName, token, ct);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Verifica le credenziali e apre una sessione: access token nel corpo,
    /// refresh token nel cookie. Con la 2FA attiva risponde invece 202 con una
    /// sfida, da completare su <c>/auth/login/2fa</c>.
    /// </summary>
    /// <remarks>
    /// Email inesistente e password sbagliata danno lo stesso 401. L'email non
    /// confermata invece ha il suo 403: lo vede solo chi conosce già la
    /// password, quindi non rivela niente a un estraneo, e permette al frontend
    /// di proporre il reinvio del link.
    /// </remarks>
    private static async Task<IResult> Login(
        LoginRequest req, FlarelyticsDbContext db, TokenService tokens, RefreshTokenService sessions,
        RefreshCookie cookie, HttpResponse response, CancellationToken ct)
    {
        var email = User.NormalizeEmail(req.Email);
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct);

        if (user is null || !user.VerifyPassword(req.Password))
        {
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "invalid_credentials", "Email o password non corrette.");
        }

        if (!user.IsEmailConfirmed)
        {
            throw new ApiProblem(StatusCodes.Status403Forbidden, "email_not_confirmed", "Conferma l'email prima di accedere.");
        }

        var now = DateTime.UtcNow;

        if (user.IsTwoFactorEnabled)
        {
            var challenge = SecureToken.Create();
            db.Add(LoginChallenge.Issue(user.Id, challenge, now));
            await db.SaveChangesAsync(ct);

            return Results.Accepted(value: new SecondFactorChallenge(challenge, LoginChallenge.Lifetime));
        }

        return await OpenSessionAsync(user, db, tokens, sessions, cookie, response, now, ct);
    }

    /// <summary>
    /// Il secondo passo dell'accesso: la sfida ricevuta dal login e un codice
    /// dell'app, oppure un codice di recupero.
    /// </summary>
    /// <remarks>
    /// Ogni sfida ammette cinque errori, poi si brucia: con sei cifre, senza un
    /// tetto, il codice si troverebbe per tentativi.
    /// </remarks>
    private static async Task<IResult> LoginSecondFactor(
        SecondFactorRequest req, FlarelyticsDbContext db, TokenService tokens, RefreshTokenService sessions,
        RefreshCookie cookie, TwoFactorService twoFactor, HttpResponse response, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hash = RefreshToken.Hash(req.ChallengeToken);
        var challenge = await db.Set<LoginChallenge>().SingleOrDefaultAsync(c => c.TokenHash == hash, ct);

        if (challenge is null || !challenge.IsUsable(now))
        {
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "challenge_expired", "Il tempo per il codice è scaduto: accedi di nuovo.");
        }

        var user = await db.Set<User>().SingleAsync(u => u.Id == challenge.UserId, ct);

        if (!twoFactor.VerifyCodeOrRecovery(user, req.Code, now))
        {
            challenge.RecordFailure();
            await db.SaveChangesAsync(ct);
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "invalid_code", "Il codice non è corretto.");
        }

        challenge.Complete(now);
        return await OpenSessionAsync(user, db, tokens, sessions, cookie, response, now, ct);
    }

    private static async Task<IResult> OpenSessionAsync(
        User user, FlarelyticsDbContext db, TokenService tokens, RefreshTokenService sessions,
        RefreshCookie cookie, HttpResponse response, DateTime now, CancellationToken ct)
    {
        user.RegisterLogin(now);
        var refresh = sessions.Issue(user, now);
        await db.SaveChangesAsync(ct);

        cookie.Write(response, refresh, now);
        return Results.Ok(Session(tokens, user, now));
    }

    /// <summary>
    /// Scambia il cookie di sessione con un access token nuovo, e il cookie con
    /// uno nuovo. È la chiamata che il frontend fa all'avvio e prima che
    /// l'access token scada.
    /// </summary>
    /// <remarks>
    /// Il frontend deve averne una sola in volo per volta: due rinnovi paralleli
    /// con lo stesso cookie fanno sembrare il secondo un furto, e chiudono tutte
    /// le sessioni.
    /// </remarks>
    private static async Task<IResult> Refresh(
        HttpRequest request, HttpResponse response, FlarelyticsDbContext db, TokenService tokens,
        RefreshTokenService sessions, RefreshCookie cookie, CancellationToken ct)
    {
        var current = cookie.Read(request);
        if (current is null) throw new ApiProblem(StatusCodes.Status401Unauthorized, "no_session", "Nessuna sessione aperta.");

        var now = DateTime.UtcNow;
        var rotated = await sessions.RotateAsync(current, now, ct);

        // Si salva anche quando va male: il riuso rilevato revoca le sessioni.
        await db.SaveChangesAsync(ct);

        if (rotated is not var (user, next))
        {
            cookie.Clear(response);
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "session_expired", "La sessione è scaduta: accedi di nuovo.");
        }

        cookie.Write(response, next, now);
        return Results.Ok(Session(tokens, user, now));
    }

    private static async Task<IResult> Logout(
        HttpRequest request, HttpResponse response, FlarelyticsDbContext db, RefreshTokenService sessions,
        RefreshCookie cookie, CancellationToken ct)
    {
        if (cookie.Read(request) is { } current)
        {
            await sessions.RevokeAsync(current, DateTime.UtcNow, ct);
            await db.SaveChangesAsync(ct);
        }

        cookie.Clear(response);
        return Results.NoContent();
    }

    /// <summary>Sempre 202, per lo stesso motivo del reinvio della conferma.</summary>
    private static async Task<IResult> ForgotPassword(
        EmailRequest req, FlarelyticsDbContext db, AccountEmails emails, CancellationToken ct)
    {
        var email = User.NormalizeEmail(req.Email);
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct);

        if (user is not null)
        {
            var token = await ReplaceTokenAsync(db, user, EmailTokenPurpose.ResetPassword, ResetLifetime, ct);
            await emails.SendPasswordResetAsync(user.Email, user.FullName, token, ct);
        }

        return Results.Accepted();
    }

    /// <summary>
    /// Imposta la password nuova e chiude tutte le sessioni: chi l'ha chiesto
    /// potrebbe farlo proprio perché qualcun altro conosceva quella vecchia.
    /// </summary>
    private static async Task<IResult> ResetPassword(
        ResetPasswordRequest req, FlarelyticsDbContext db, RefreshTokenService sessions, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = await FindUsableTokenAsync(db, req.Token, EmailTokenPurpose.ResetPassword, now, ct);
        if (token is null) throw ApiProblem.BadRequest("invalid_token", "Il link non è valido o è scaduto.");

        var user = await db.Set<User>().SingleAsync(u => u.Id == token.UserId, ct);
        user.ChangePassword(req.Password);

        // Aver ricevuto il link prova anche il possesso della casella.
        user.ConfirmEmail(now);
        token.MarkUsed(now);
        await sessions.RevokeAllAsync(user.Id, now, ct);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    internal static SessionResponse Session(TokenService tokens, User user, DateTime now)
    {
        var (accessToken, expires) = tokens.Create(user, now);
        return new SessionResponse(accessToken, expires, new SessionUser(user.Id, user.Email, user.FullName));
    }

    private static async Task<EmailToken?> FindUsableTokenAsync(
        FlarelyticsDbContext db, string token, EmailTokenPurpose purpose, DateTime now, CancellationToken ct)
    {
        var hash = RefreshToken.Hash(token);
        var found = await db.Set<EmailToken>().SingleOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, ct);
        return found is not null && found.IsUsable(now) ? found : null;
    }

    /// <summary>
    /// Invalida i link precedenti dello stesso tipo e ne emette uno nuovo: vale
    /// sempre solo l'ultimo email ricevuto.
    /// </summary>
    private static async Task<string> ReplaceTokenAsync(
        FlarelyticsDbContext db, User user, EmailTokenPurpose purpose, TimeSpan lifetime, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var previous = await db.Set<EmailToken>()
            .Where(t => t.UserId == user.Id && t.Purpose == purpose && t.UsedAtUtc == null)
            .ToListAsync(ct);
        foreach (var p in previous) p.MarkUsed(now);

        var token = SecureToken.Create();
        db.Add(EmailToken.Issue(user.Id, purpose, token, now, lifetime));
        await db.SaveChangesAsync(ct);
        return token;
    }
}

/// <param name="OrganizationName">Obbligatorio senza invito; con l'invito si ignora.</param>
/// <param name="Plan">Codice del piano: <c>starter</c> se assente. Con l'invito si ignora.</param>
/// <param name="InvitationToken">Il token del link d'invito, se ci si registra per entrare in un'organizzazione esistente.</param>
public record RegisterRequest(string Email, string Password, string FullName, string? OrganizationName, string? Plan, string? InvitationToken = null);

/// <param name="CheckoutUrl">Null finché i pagamenti sono in sordina.</param>
/// <param name="EmailConfirmationRequired">False con l'invito: si può accedere subito.</param>
public record RegisterResponse(Guid UserId, Guid OrganizationId, string? CheckoutUrl, bool EmailConfirmationRequired);

/// <param name="ChallengeToken">Da rimandare con il codice su <c>/auth/login/2fa</c>.</param>
public record SecondFactorChallenge(string ChallengeToken, TimeSpan ExpiresIn)
{
    public bool TwoFactorRequired => true;
}

/// <param name="Code">Le 6 cifre dell'app, oppure un codice di recupero.</param>
public record SecondFactorRequest(string ChallengeToken, string Code);

public record LoginRequest(string Email, string Password);
public record EmailRequest(string Email);
public record TokenRequest(string Token);
public record ResetPasswordRequest(string Token, string Password);

/// <param name="AccessToken">Da mandare come <c>Authorization: Bearer</c>. Va tenuto in memoria, non in localStorage.</param>
/// <param name="AccessTokenExpiresAtUtc">Per rinnovare un po' prima invece di aspettare il 401.</param>
public record SessionResponse(string AccessToken, DateTime AccessTokenExpiresAtUtc, SessionUser User);
public record SessionUser(Guid Id, string Email, string FullName);

public static class PasswordRules
{
    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(10).WithMessage("La password deve avere almeno 10 caratteri.")
            .MaximumLength(72).WithMessage("La password può avere al massimo 72 caratteri.");
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        // 72 è il limite di BCrypt: oltre, i caratteri in più vengono ignorati
        // senza dirlo, e due password diverse risulterebbero uguali.
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OrganizationName).NotEmpty().When(x => x.InvitationToken is null).MaximumLength(100);
        RuleFor(x => x.Plan).Must(p => p is null || Plans.Find(p) is not null).WithMessage("Piano sconosciuto.");
        RuleFor(x => x.InvitationToken).MaximumLength(100);
    }
}

public class SecondFactorRequestValidator : AbstractValidator<SecondFactorRequest>
{
    public SecondFactorRequestValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

public class EmailRequestValidator : AbstractValidator<EmailRequest>
{
    public EmailRequestValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
}

public class TokenRequestValidator : AbstractValidator<TokenRequest>
{
    public TokenRequestValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(100);
}

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).StrongPassword();
    }
}
