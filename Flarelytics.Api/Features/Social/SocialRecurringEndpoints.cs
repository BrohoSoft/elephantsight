using System.Globalization;
using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Social;

/// <summary>
/// I post ricorrenti. Rotte sotto <c>/orgs/{orgId}/social/recurring</c>.
/// </summary>
/// <remarks>
/// Un post ricorrente non ha uscite preparate in anticipo: ogni uscita la crea
/// il <see cref="SocialPublishWorker"/> quando arriva l'ora (vedi
/// <see cref="RecurringPostScheduler"/>). Per questo una modifica vale da
/// subito per tutte le uscite future, e il calendario le mostra calcolandole
/// dalla regola (<c>/occurrences</c>). Come un post programmato, si controlla
/// contro i limiti delle reti quando si salva.
/// </remarks>
public static class SocialRecurringEndpoints
{
    public static void MapSocialRecurring(this IEndpointRouteBuilder api)
    {
        var social = api.MapOrgGroup("/social");
        social.MapGet("/recurring", List);
        social.MapGet("/recurring/occurrences", Occurrences);

        var admin = social.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapPost("/recurring", Create).Validating<SaveRecurringPostRequest>();
        admin.MapPut("/recurring/{recurringId:guid}", Update).Validating<SaveRecurringPostRequest>();
        admin.MapPost("/recurring/{recurringId:guid}/paused", SetPaused);
        admin.MapDelete("/recurring/{recurringId:guid}", Delete);
    }

    private static async Task<IResult> List(FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var list = await db.Set<SocialRecurringPost>().AsNoTracking().Include(r => r.Media)
            .OrderBy(r => r.IsPaused).ThenBy(r => r.NextOccurrenceUtc == null).ThenBy(r => r.NextOccurrenceUtc).ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(ct);

        // L'ultima uscita di ogni serie, per mostrare com'è andata.
        var last = await db.Set<SocialPost>().AsNoTracking().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => p.RecurringPostId != null &&
                        p.ScheduledAtUtc == db.Set<SocialPost>().Where(q => q.RecurringPostId == p.RecurringPostId).Max(q => q.ScheduledAtUtc))
            .ToListAsync(ct);

        return Results.Ok(list.Select(r => RecurringPostResponse.From(r, signer, last.FirstOrDefault(p => p.RecurringPostId == r.Id))));
    }

    /// <summary>Le uscite future fra due istanti, per il calendario. Quelle passate sono già post veri.</summary>
    private static async Task<IResult> Occurrences(DateTime from, DateTime to, Guid? projectId, FlarelyticsDbContext db, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(100)) throw ApiProblem.BadRequest("range", "Un periodo di al massimo 100 giorni.");
        var now = DateTime.UtcNow;
        var start = DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc);
        if (start < now) start = now;

        var query = db.Set<SocialRecurringPost>().AsNoTracking().Where(r => !r.IsPaused && r.NextOccurrenceUtc != null);
        if (projectId is { } pid) query = query.Where(r => r.ProjectId == pid);

        var occurrences = (await query.ToListAsync(ct))
            .SelectMany(r => start < end ? r.Rule.Between(start, end).Select(at => new RecurringOccurrence(r.Id, at)) : [])
            .OrderBy(o => o.AtUtc)
            .ToList();
        return Results.Ok(occurrences);
    }

    private static async Task<IResult> Create(SaveRecurringPostRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        MediaUrlSigner signer, SocialMediaStorage storage, CancellationToken ct)
    {
        var recurring = SocialRecurringPost.Create(org.TenantId, principal.UserId());
        db.Add(recurring);
        await ApplyAsync(recurring, req, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{org.TenantId}/social/recurring/{recurring.Id}",
            RecurringPostResponse.From(recurring, signer, null));
    }

    private static async Task<IResult> Update(Guid recurringId, SaveRecurringPostRequest req, FlarelyticsDbContext db, MediaUrlSigner signer,
        SocialMediaStorage storage, CancellationToken ct)
    {
        var recurring = await LoadAsync(db, recurringId, ct);
        await ApplyAsync(recurring, req, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(RecurringPostResponse.From(recurring, signer, null));
    }

    private static async Task<IResult> SetPaused(Guid recurringId, SetPausedRequest req, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var recurring = await LoadAsync(db, recurringId, ct);
        recurring.SetPaused(req.Paused, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(RecurringPostResponse.From(recurring, signer, null));
    }

    /// <summary>Le uscite già fatte restano sul calendario, come post normali; i file della serie si cancellano.</summary>
    private static async Task<IResult> Delete(Guid recurringId, FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        var recurring = await LoadAsync(db, recurringId, ct);
        var media = recurring.Media.ToList();
        db.Remove(recurring);
        await db.SaveChangesAsync(ct);
        foreach (var m in media) storage.Delete(m);
        return Results.NoContent();
    }

    /// <summary>Scrive contenuto, account, file e regola della richiesta, e controlla tutto contro i limiti delle reti.</summary>
    private static async Task ApplyAsync(SocialRecurringPost recurring, SaveRecurringPostRequest req, FlarelyticsDbContext db, SocialMediaStorage storage,
        CancellationToken ct)
    {
        if (req.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
            throw ApiProblem.NotFound("Progetto");

        var accounts = await db.Set<SocialAccount>().Where(a => req.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != req.AccountIds.Distinct().Count()) throw ApiProblem.NotFound("Account");
        if (accounts.Count == 0) throw ApiProblem.BadRequest("no_accounts", "Scegli almeno un account su cui pubblicare.");

        var options = req.Options ?? new PostOptions();
        recurring.Update(req.Text, req.ProjectId, req.AccountIds, options);

        // I file: nell'ordine della richiesta. Quelli tolti si cancellano. Si
        // prendono solo file appena caricati o già di questa serie.
        var mediaIds = req.Media.Select(m => m.Id).ToList();
        var media = await db.Set<SocialMedia>()
            .Where(m => mediaIds.Contains(m.Id) && m.PostId == null && (m.RecurringPostId == null || m.RecurringPostId == recurring.Id))
            .ToListAsync(ct);
        if (media.Count != mediaIds.Distinct().Count()) throw ApiProblem.NotFound("Immagine");

        foreach (var m in recurring.Media.Where(m => !mediaIds.Contains(m.Id)).ToList())
        {
            recurring.RemoveMedia(m);
            db.Remove(m);
            storage.Delete(m);
        }
        foreach (var (item, position) in req.Media.Select((m, i) => (m, i)))
        {
            var m = media.Single(x => x.Id == item.Id);
            m.AttachToRecurring(recurring.Id, position, item.AltText);
            if (!recurring.Media.Contains(m)) recurring.AddMedia(m);
        }

        var problems = SocialPostEndpoints.ProblemsFor(recurring.Text, recurring.Media.OrderBy(m => m.Position).ToList(), accounts, _ => null, options);
        if (problems.Count > 0) throw ApiProblem.BadRequest("post_invalid", string.Join(" ", problems));

        var now = DateTime.UtcNow;
        recurring.SetRule(RuleFrom(req), now);
        if (recurring.NextOccurrenceUtc is null)
            throw ApiProblem.BadRequest("recurrence_ended", "Con queste date non resta nessuna uscita: sposta la fine più avanti.");
        recurring.SetPaused(req.IsPaused, now);
    }

    private static RecurrenceRule RuleFrom(SaveRecurringPostRequest req) => new(
        req.Frequency, req.Interval,
        req.Frequency == RecurrenceFrequency.Weekly ? (req.DaysOfWeek ?? []).Aggregate(0, (bits, d) => bits | RecurrenceRule.Bit(d)) : 0,
        TimeOnly.ParseExact(req.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture), req.TimeZone, req.StartDate, req.EndDate);

    private static async Task<SocialRecurringPost> LoadAsync(FlarelyticsDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<SocialRecurringPost>().Include(r => r.Media).SingleOrDefaultAsync(r => r.Id == id, ct)
        ?? throw ApiProblem.NotFound("Post ricorrente");
}

/// <param name="Upcoming">Le prossime uscite (vuoto se in pausa o finita).</param>
/// <param name="LastPost">L'ultima uscita, con il suo esito.</param>
public record RecurringPostResponse(Guid Id, string Text, Guid? ProjectId, IReadOnlyList<Guid> AccountIds, PostOptions Options,
    IReadOnlyList<SocialMediaResponse> Media, RecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> DaysOfWeek, string TimeOfDay,
    string TimeZone, DateOnly StartDate, DateOnly? EndDate, bool IsPaused, DateTime? NextOccurrenceUtc, IReadOnlyList<DateTime> Upcoming,
    int OccurrenceCount, SocialPostResponse? LastPost, DateTime CreatedAtUtc)
{
    /// <summary>Quante prossime uscite mostra l'elenco.</summary>
    public const int UpcomingCount = 5;

    public static RecurringPostResponse From(SocialRecurringPost r, MediaUrlSigner signer, SocialPost? last)
    {
        var upcoming = new List<DateTime>();
        if (!r.IsPaused && r.NextOccurrenceUtc is { } next)
        {
            upcoming.Add(next);
            while (upcoming.Count < UpcomingCount && r.Rule.NextAfter(upcoming[^1]) is { } after) upcoming.Add(after);
        }

        return new(r.Id, r.Text, r.ProjectId, r.AccountIds, r.Options,
            r.Media.OrderBy(m => m.Position).Select(m => SocialMediaResponse.From(m, signer)).ToList(),
            r.Frequency, r.Interval, Enum.GetValues<DayOfWeek>().Where(d => (r.DaysOfWeek & RecurrenceRule.Bit(d)) != 0).ToList(),
            r.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture), r.TimeZone, r.StartDate, r.EndDate, r.IsPaused,
            r.IsPaused ? null : r.NextOccurrenceUtc, upcoming, r.OccurrenceCount, last is null ? null : SocialPostResponse.From(last, signer), r.CreatedAtUtc);
    }
}

public record RecurringOccurrence(Guid RecurringPostId, DateTime AtUtc);

public record SetPausedRequest(bool Paused);

/// <param name="Interval">Ogni quanti giorni, settimane o mesi.</param>
/// <param name="DaysOfWeek">Solo settimanale: i giorni dell'uscita.</param>
/// <param name="TimeOfDay">"HH:mm", nel fuso <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">Fuso IANA, quello del browser (Europe/Rome).</param>
/// <param name="StartDate">Il primo giorno; per il mensile anche il giorno del mese.</param>
/// <param name="EndDate">L'ultimo giorno (compreso), null = senza fine.</param>
public record SaveRecurringPostRequest(string Text, Guid? ProjectId, IReadOnlyList<Guid> AccountIds, IReadOnlyList<SavePostMedia> Media, PostOptions? Options,
    RecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek>? DaysOfWeek, string TimeOfDay, string TimeZone, DateOnly StartDate, DateOnly? EndDate,
    bool IsPaused);

public class SaveRecurringPostRequestValidator : AbstractValidator<SaveRecurringPostRequest>
{
    public SaveRecurringPostRequestValidator()
    {
        RuleFor(x => x.Text).NotNull().MaximumLength(10000);
        RuleFor(x => x.AccountIds).NotNull().Must(a => a.Count <= 50);
        RuleFor(x => x.Media).NotNull().Must(m => m.Count <= 10).WithMessage("Al massimo 10 immagini.");
        RuleForEach(x => x.Media).ChildRules(m => m.RuleFor(x => x.AltText).MaximumLength(1500));
        RuleFor(x => x.Frequency).IsInEnum();
        RuleFor(x => x.Interval).InclusiveBetween(1, 365).WithMessage("Ogni quanto: da 1 a 365.");
        RuleFor(x => x.DaysOfWeek).Must(d => d is { Count: > 0 }).When(x => x.Frequency == RecurrenceFrequency.Weekly)
            .WithMessage("Scegli almeno un giorno della settimana.");
        RuleFor(x => x.TimeOfDay).NotEmpty().Must(t => TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("L'ora va scritta come HH:mm.");
        RuleFor(x => x.TimeZone).NotEmpty().MaximumLength(100).Must(RecurrenceRule.IsValidTimeZone).WithMessage("Fuso orario sconosciuto.");
        RuleFor(x => x.EndDate).Must((x, end) => end is null || end >= x.StartDate).WithMessage("La fine viene prima dell'inizio.");
    }
}
