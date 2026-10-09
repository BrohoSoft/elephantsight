using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Email;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Orgs;

/// <summary>
/// Membri e inviti di un'organizzazione, più le due rotte pubbliche per
/// leggere e accettare un invito.
/// </summary>
/// <remarks>
/// <para>Membership e inviti non hanno il filtro automatico sul tenant (vedi
/// <see cref="Invitation"/>): qui ogni query filtra per
/// <c>CurrentOrg.TenantId</c> a mano. È l'unico file in cui succede.</para>
///
/// <para>Le regole sui ruoli:</para>
/// <list type="bullet">
/// <item>gli admin invitano e rimuovono admin e viewer; solo un owner tocca
/// gli owner, e solo un owner nomina un owner;</item>
/// <item>chiunque può uscire da solo;</item>
/// <item>progetti e sezioni (vedi <see cref="MemberAccess"/>) li sceglie un
/// admin con accesso completo, anche al momento dell'invito; un owner vede
/// sempre tutto, e l'accesso di un owner non si restringe;</item>
/// <item>l'ultimo owner non può né uscire né essere declassato:
/// un'organizzazione senza owner non avrebbe nessuno che può gestire
/// l'abbonamento.</item>
/// </list>
/// </remarks>
public static class MemberEndpoints
{
    public static void MapMembers(this IEndpointRouteBuilder api)
    {
        api.MapGet("/invitations/preview", Preview).AllowAnonymous().RequireRateLimiting(Auth.AuthEndpoints.RateLimitPolicy);
        api.MapPost("/invitations/accept", Accept).RequireAuthorization().Validating<AcceptInvitationRequest>();

        var org = api.MapOrgGroup();
        // Chi vede solo alcuni progetti non vede gli altri membri (potrebbero
        // essere clienti diversi): può solo uscire da solo.
        org.MapGet("/members", ListMembers).RequireFullAccess();
        org.MapPut("/members/{userId:guid}", ChangeRole).RequireOrgRole(OrgRole.Owner).Validating<ChangeRoleRequest>();
        org.MapPut("/members/{userId:guid}/access", ChangeAccess).RequireOrgRole(OrgRole.Admin).RequireFullAccess().Validating<AccessRequest>();
        org.MapDelete("/members/{userId:guid}", RemoveMember);

        var invitations = org.MapGroup("/invitations").RequireOrgRole(OrgRole.Admin).RequireFullAccess();
        invitations.MapGet("", ListInvitations);
        invitations.MapPost("", Invite).Validating<InviteRequest>();
        invitations.MapDelete("/{invitationId:guid}", RevokeInvitation);
    }

    private static async Task<IResult> ListMembers(CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        // Ordinamento prima della proiezione: EF non sa tradurre un OrderBy
        // sulle proprietà di un record costruito nella Select.
        var members = await db.Set<Membership>().AsNoTracking()
            .Where(m => m.TenantId == org.TenantId)
            .Join(db.Set<User>(), m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderByDescending(x => x.m.Role).ThenBy(x => x.u.FullName)
            .Select(x => new { x.u.Id, x.u.Email, x.u.FullName, x.m.Role, x.m.AllProjects, x.m.ProjectIds, x.m.Sections, x.m.CreatedAtUtc })
            .ToListAsync(ct);

        return Results.Ok(members.Select(x => new MemberResponse(x.Id, x.Email, x.FullName, x.Role,
            AccessResponse.From(new MemberAccess(x.AllProjects, x.ProjectIds, x.Sections)), x.CreatedAtUtc)));
    }

    private static async Task<IResult> ChangeRole(
        Guid userId, ChangeRoleRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(db, org, userId, ct);

        if (membership.Role == OrgRole.Owner && req.Role != OrgRole.Owner)
        {
            await EnsureNotLastOwnerAsync(db, org, ct);
        }

        membership.ChangeRole(req.Role);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>Progetti e sezioni di un membro. Quello di un owner non si restringe.</summary>
    private static async Task<IResult> ChangeAccess(
        Guid userId, AccessRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(db, org, userId, ct);
        if (membership.Role == OrgRole.Owner)
            throw ApiProblem.Conflict("owner_access", "Un owner vede sempre tutta l'organizzazione: per limitarlo, prima cambia il suo ruolo.");

        membership.SetAccess(await ValidAccessAsync(req, db, ct));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>L'accesso della richiesta, con progetti che esistono davvero in questa organizzazione.</summary>
    private static async Task<MemberAccess> ValidAccessAsync(AccessRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var ids = req.AllProjects ? [] : (req.ProjectIds ?? []).Distinct().ToList();
        if (ids.Count > 0 && await db.Set<Project>().CountAsync(p => ids.Contains(p.Id), ct) != ids.Count)
            throw ApiProblem.NotFound("Progetto");
        return new MemberAccess(req.AllProjects, ids, req.Sections);
    }

    private static async Task<IResult> RemoveMember(
        Guid userId, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(db, org, userId, ct);
        var leaving = userId == principal.UserId();

        if (!leaving)
        {
            if (org.Role < OrgRole.Admin || !org.IsFull)
                throw ApiProblem.Forbidden("Solo admin e owner possono rimuovere altri membri.");
            if (membership.Role == OrgRole.Owner && org.Role < OrgRole.Owner)
                throw ApiProblem.Forbidden("Solo un owner può rimuovere un altro owner.");
        }

        if (membership.Role == OrgRole.Owner) await EnsureNotLastOwnerAsync(db, org, ct);

        db.Remove(membership);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListInvitations(CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var pending = await db.Set<Invitation>().AsNoTracking()
            .Where(i => i.TenantId == org.TenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
            .OrderBy(i => i.Email)
            .ToListAsync(ct);

        return Results.Ok(pending.Select(i => new InvitationResponse(i.Id, i.Email, i.Role, AccessResponse.From(i.Access), i.CreatedAtUtc, i.ExpiresAtUtc)));
    }

    /// <summary>
    /// Crea l'invito e, se l'email è configurata, lo manda. Un invito ancora
    /// aperto per lo stesso indirizzo viene sostituito: vale sempre e solo
    /// l'ultimo link.
    /// </summary>
    /// <remarks>
    /// Il link torna anche nella risposta, una volta sola: in un'installazione
    /// senza SMTP è l'unico modo di farlo arrivare, e chi invita lo copia e lo
    /// manda come preferisce.
    /// </remarks>
    private static async Task<IResult> Invite(
        InviteRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        AccountEmails emails, Microsoft.Extensions.Options.IOptions<Flarelytics.Api.Auth.AuthOptions> auth, CancellationToken ct)
    {
        if (req.Role == OrgRole.Owner && org.Role < OrgRole.Owner)
        {
            throw ApiProblem.Forbidden("Solo un owner può invitare un altro owner.");
        }

        var email = User.NormalizeEmail(req.Email);
        var alreadyMember = await db.Set<Membership>()
            .Where(m => m.TenantId == org.TenantId)
            .Join(db.Set<User>(), m => m.UserId, u => u.Id, (m, u) => u.Email)
            .AnyAsync(e => e == email, ct);

        if (alreadyMember) throw ApiProblem.Conflict("already_member", "Questa persona fa già parte dell'organizzazione.");

        var now = DateTime.UtcNow;
        var previous = await db.Set<Invitation>()
            .Where(i => i.TenantId == org.TenantId && i.Email == email && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var p in previous) p.Revoke(now);

        var inviterId = principal.UserId();
        var inviter = await db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == inviterId, ct);
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == org.TenantId, ct);

        var access = req.Access is { } a ? await ValidAccessAsync(a, db, ct) : MemberAccess.Full;
        var token = SecureToken.Create();
        var invitation = Invitation.Create(org.TenantId, email, req.Role, inviterId, token, now, access);
        db.Add(invitation);
        await db.SaveChangesAsync(ct);

        var sent = await emails.SendInvitationAsync(email, tenant.Name, inviter.FullName, token, ct);
        var link = $"{auth.Value.PublicAppUrl.TrimEnd('/')}/accept-invite?token={Uri.EscapeDataString(token)}";

        return Results.Created($"/api/v1/orgs/{org.TenantId}/invitations/{invitation.Id}",
            new CreatedInvitationResponse(invitation.Id, invitation.Email, invitation.Role, AccessResponse.From(invitation.Access), invitation.ExpiresAtUtc, link, sent));
    }

    private static async Task<IResult> RevokeInvitation(Guid invitationId, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var invitation = await db.Set<Invitation>()
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.TenantId == org.TenantId, ct)
            ?? throw ApiProblem.NotFound("Invito");

        invitation.Revoke(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Cosa dice un invito, per chi apre il link: serve al frontend per
    /// scegliere fra "accedi e accetta" e "registrati".
    /// </summary>
    /// <remarks>
    /// <c>AccountExists</c> dice se l'indirizzo ha già un account. Lo vede solo
    /// chi ha il link, che è mandato a quell'indirizzo.
    /// </remarks>
    private static async Task<IResult> Preview(string token, FlarelyticsDbContext db, CancellationToken ct)
    {
        var invitation = await Invitations.FindPendingAsync(db, token, DateTime.UtcNow, ct);
        var inviter = await db.Set<User>().AsNoTracking().Where(u => u.Id == invitation.InvitedByUserId).Select(u => u.FullName).SingleAsync(ct);
        var accountExists = await db.Set<User>().AnyAsync(u => u.Email == invitation.Email, ct);

        return Results.Ok(new InvitationPreview(invitation.Tenant.Name, invitation.Email, invitation.Role, inviter, accountExists));
    }

    /// <summary>Un utente già registrato accetta l'invito. L'invito è nominativo: l'email deve essere la sua.</summary>
    private static async Task<IResult> Accept(
        AcceptInvitationRequest req, ClaimsPrincipal principal, FlarelyticsDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var invitation = await Invitations.FindPendingAsync(db, req.Token, now, ct);
        var userId = principal.UserId();
        var user = await db.Set<User>().SingleAsync(u => u.Id == userId, ct);

        if (user.Email != invitation.Email)
        {
            throw new ApiProblem(StatusCodes.Status403Forbidden, "invitation_email_mismatch",
                $"L'invito è per {invitation.Email}: accedi con quell'account per accettarlo.");
        }

        invitation.Accept(now);
        if (!await db.Set<Membership>().AnyAsync(m => m.TenantId == invitation.TenantId && m.UserId == userId, ct))
        {
            db.Add(Membership.Create(invitation.TenantId, userId, invitation.Role, invitation.Access));
        }

        // Il link è arrivato a quella casella: vale come conferma dell'email.
        user.ConfirmEmail(now);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AcceptInvitationResponse(invitation.TenantId));
    }

    private static async Task<Membership> FindMembershipAsync(FlarelyticsDbContext db, CurrentOrg org, Guid userId, CancellationToken ct) =>
        await db.Set<Membership>().SingleOrDefaultAsync(m => m.TenantId == org.TenantId && m.UserId == userId, ct)
        ?? throw ApiProblem.NotFound("Membro");

    private static async Task EnsureNotLastOwnerAsync(FlarelyticsDbContext db, CurrentOrg org, CancellationToken ct)
    {
        var owners = await db.Set<Membership>().CountAsync(m => m.TenantId == org.TenantId && m.Role == OrgRole.Owner, ct);
        if (owners <= 1)
        {
            throw ApiProblem.Conflict("last_owner",
                "È l'ultimo owner dell'organizzazione: nomina prima un altro owner.");
        }
    }
}

/// <summary>Ricerca degli inviti per token, condivisa con la registrazione.</summary>
public static class Invitations
{
    public static async Task<Invitation> FindPendingAsync(FlarelyticsDbContext db, string token, DateTime now, CancellationToken ct)
    {
        var hash = Core.Database.Entities.RefreshToken.Hash(token);
        var invitation = await db.Set<Invitation>().Include(i => i.Tenant).SingleOrDefaultAsync(i => i.TokenHash == hash, ct);

        return invitation is not null && invitation.IsPending(now)
            ? invitation
            : throw new ApiProblem(StatusCodes.Status410Gone, "invitation_invalid",
                "L'invito non è più valido: è scaduto, è già stato usato o è stato revocato. Chiedine uno nuovo.");
    }
}

/// <summary>Cosa vede un membro: tutti i progetti o quelli elencati, e le sezioni (Store, Social).</summary>
public record AccessResponse(bool AllProjects, IReadOnlyList<Guid> ProjectIds, bool Store, bool Social)
{
    public static AccessResponse From(MemberAccess a) =>
        new(a.AllProjects, a.ProjectIds, a.Sections.HasFlag(AppSections.Store), a.Sections.HasFlag(AppSections.Social));
}

/// <param name="Sections">Store, Social o "Store, Social".</param>
public record AccessRequest(bool AllProjects, IReadOnlyList<Guid>? ProjectIds, AppSections Sections);

public record MemberResponse(Guid UserId, string Email, string FullName, OrgRole Role, AccessResponse Access, DateTime JoinedAtUtc);
public record ChangeRoleRequest(OrgRole Role);

/// <param name="Access">Null = tutto (come prima). Per un owner si ignora: vede sempre tutto.</param>
public record InviteRequest(string Email, OrgRole Role, AccessRequest? Access = null);
public record InvitationResponse(Guid Id, string Email, OrgRole Role, AccessResponse Access, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);
/// <param name="Link">Il link da mandare. Si vede solo adesso: a database c'è solo il suo hash.</param>
/// <param name="EmailSent">False se l'installazione non ha l'email configurata: il link va mandato a mano.</param>
public record CreatedInvitationResponse(Guid Id, string Email, OrgRole Role, AccessResponse Access, DateTime ExpiresAtUtc, string Link, bool EmailSent);
public record InvitationPreview(string OrganizationName, string Email, OrgRole Role, string InvitedBy, bool AccountExists);
public record AcceptInvitationRequest(string Token);
public record AcceptInvitationResponse(Guid OrganizationId);

public class ChangeRoleRequestValidator : AbstractValidator<ChangeRoleRequest>
{
    public ChangeRoleRequestValidator() => RuleFor(x => x.Role).IsInEnum();
}

public class InviteRequestValidator : AbstractValidator<InviteRequest>
{
    public InviteRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.Role).IsInEnum();
        RuleFor(x => x.Access!).SetValidator(new AccessRequestValidator()).When(x => x.Access is not null);
    }
}

public class AccessRequestValidator : AbstractValidator<AccessRequest>
{
    public AccessRequestValidator()
    {
        RuleFor(x => x.Sections).Must(s => s != AppSections.None && (s & ~AppSections.All) == 0)
            .WithName("sections").WithMessage("Scegli almeno una sezione fra Store e Social.");
        RuleFor(x => x.ProjectIds).Must(p => p is { Count: > 0 }).When(x => !x.AllProjects)
            .WithName("projectIds").WithMessage("Scegli almeno un progetto, o tutti.");
        RuleFor(x => x.ProjectIds).Must(p => p is null || p.Count <= 200);
    }
}

public class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(100);
}
