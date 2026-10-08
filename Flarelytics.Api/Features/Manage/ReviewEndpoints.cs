using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Icons;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Management;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// Le recensioni di tutte le app dell'organizzazione, App Store e Google Play
/// in un'unica lista, con la risposta.
/// </summary>
public static class ReviewEndpoints
{
    private const int PageSize = 50;
    public const int GoogleReplyLimit = 350;

    public static void MapReviews(this IEndpointRouteBuilder api)
    {
        var reviews = api.MapOrgGroup("/reviews");
        reviews.MapGet("", List);
        reviews.MapPost("/{reviewId:guid}/reply", Reply).RequireOrgRole(OrgRole.Admin).Validating<ReplyRequest>();
    }

    /// <summary>
    /// Le recensioni delle app collegate ai progetti, dalla più recente, con i
    /// filtri della pagina e un riepilogo per stelle e per store.
    /// </summary>
    private static async Task<IResult> List(
        FlarelyticsDbContext db, Guid? projectId, Store? store, int? rating, bool? unanswered, int? page, CancellationToken ct)
    {
        var apps = await db.Set<ProjectApp>().AsNoTracking()
            .Where(a => projectId == null || a.ProjectId == projectId)
            .Join(db.Set<Project>(), a => a.ProjectId, p => p.Id, (a, p) => new { a.Store, a.ExternalAppId, ProjectId = p.Id, ProjectName = p.Name })
            .ToListAsync(ct);

        var appleIds = apps.Where(a => a.Store == Store.AppStore).Select(a => a.ExternalAppId).ToList();
        var googleIds = apps.Where(a => a.Store == Store.GooglePlay).Select(a => a.ExternalAppId).ToList();

        var scope = db.Set<Review>().AsNoTracking().Where(r =>
            (r.Store == Store.AppStore && appleIds.Contains(r.AppId)) || (r.Store == Store.GooglePlay && googleIds.Contains(r.AppId)));

        // Il riepilogo è sull'insieme del progetto (o dell'organizzazione),
        // prima dei filtri: è il quadro, non il risultato della ricerca.
        var summary = await scope.GroupBy(r => new { r.Store, r.Rating })
            .Select(g => new { g.Key.Store, g.Key.Rating, Count = g.Count() })
            .ToListAsync(ct);

        if (store is { } s) scope = scope.Where(r => r.Store == s);
        if (rating is { } stars) scope = scope.Where(r => r.Rating == stars);
        if (unanswered == true) scope = scope.Where(r => r.ReplyText == null);

        var current = Math.Max(1, page ?? 1);
        var total = await scope.CountAsync(ct);
        var items = await scope.OrderByDescending(r => r.WrittenAtUtc)
            .Skip((current - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);

        var icons = await IconEndpoints.UrlsAsync(db, apps.Select(a => (a.Store, a.ExternalAppId)), ct);
        var projectOf = apps.GroupBy(a => (a.Store, a.ExternalAppId)).ToDictionary(g => g.Key, g => g.First());

        var syncStates = await db.Set<ReviewSyncState>().AsNoTracking().ToListAsync(ct);

        return Results.Ok(new ReviewPage(
            items.Select(r =>
            {
                var project = projectOf.GetValueOrDefault((r.Store, r.AppId));
                return new ReviewItem(r.Id, r.Store, r.AppId, project?.ProjectId, project?.ProjectName, icons.GetValueOrDefault((r.Store, r.AppId)),
                    r.Rating, r.Title, r.Body, r.Author, r.Locale, r.AppVersion, r.WrittenAtUtc, r.ReplyText, r.RepliedAtUtc, r.ReplyState);
            }).ToList(),
            total, current, PageSize,
            summary.GroupBy(x => x.Store).Select(g => new RatingSummary(g.Key,
                g.Sum(x => x.Count),
                g.Sum(x => x.Count) == 0 ? 0 : Math.Round((double)g.Sum(x => x.Rating * x.Count) / g.Sum(x => x.Count), 2),
                Enumerable.Range(1, 5).Select(star => g.Where(x => x.Rating == star).Sum(x => x.Count)).ToArray())).ToList(),
            syncStates
                .Where(st => apps.Any(a => a.Store == st.Store && a.ExternalAppId == st.AppId))
                .Select(st => new ReviewSyncInfo(st.Store, st.AppId, st.LastSyncedAtUtc, st.LastError)).ToList()));
    }

    private static async Task<IResult> Reply(Guid reviewId, ReplyRequest req, FlarelyticsDbContext db, ReviewsService reviews, CancellationToken ct)
    {
        var review = await db.Set<Review>().SingleOrDefaultAsync(r => r.Id == reviewId, ct) ?? throw ApiProblem.NotFound("Recensione");

        if (review.Store == Store.GooglePlay && req.Text.Trim().Length > GoogleReplyLimit)
        {
            throw ApiProblem.BadRequest("reply_too_long", $"Google Play accetta risposte di al massimo {GoogleReplyLimit} caratteri.");
        }

        var credential = await db.Set<ProjectApp>().Where(a => a.Store == review.Store && a.ExternalAppId == review.AppId)
            .Select(a => a.Credential).FirstOrDefaultAsync(ct)
            ?? throw ApiProblem.Conflict("app_not_linked", "L'app di questa recensione non è più collegata a un progetto: non c'è una chiave con cui rispondere.");

        await reviews.ReplyAsync(review, credential, req.Text.Trim(), ct);
        return Results.Ok(new { review.ReplyText, review.RepliedAtUtc, review.ReplyState });
    }
}

public record ReviewItem(
    Guid Id, Store Store, string AppId, Guid? ProjectId, string? ProjectName, string? IconUrl,
    int Rating, string? Title, string Body, string? Author, string? Locale, string? AppVersion, DateTime WrittenAtUtc,
    string? ReplyText, DateTime? RepliedAtUtc, string? ReplyState);

/// <param name="Distribution">Quante recensioni per 1, 2, 3, 4, 5 stelle.</param>
public record RatingSummary(Store Store, int Count, double Average, int[] Distribution);

public record ReviewSyncInfo(Store Store, string AppId, DateTime? LastSyncedAtUtc, string? LastError);

public record ReviewPage(IReadOnlyList<ReviewItem> Items, int Total, int Page, int PageSize, IReadOnlyList<RatingSummary> Summary, IReadOnlyList<ReviewSyncInfo> Sync);

/// <param name="Text">Apple accetta fino a 5970 caratteri, Google 350: vale il limite più stretto dello store della recensione.</param>
public record ReplyRequest(string Text);

public class ReplyRequestValidator : AbstractValidator<ReplyRequest>
{
    public ReplyRequestValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(5970);
}
