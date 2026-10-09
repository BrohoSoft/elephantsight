using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Social;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Api.Features.PublicApi;

/// <summary>
/// L'API per i programmi esterni: mandano post (testo, immagini, una data
/// proposta) nella coda "Da programmare". Rotte sotto <c>/public</c>, con
/// <c>Authorization: Bearer wsk_…</c>.
/// </summary>
/// <remarks>
/// La chiave non sceglie account né pubblica: decide chi usa il pannello,
/// dalla coda. Così una chiave rubata può al massimo riempire la coda, non
/// parlare a nome dell'azienda sui social.
/// </remarks>
public static class PublicPostEndpoints
{
    public static void MapPublicApi(this IEndpointRouteBuilder api)
    {
        var group = api.MapApiKeyGroup("");
        group.MapPost("/media", UploadMedia).DisableAntiforgery();
        group.MapPost("/posts", Create).Validating<PublicPostRequest>();
        group.MapGet("/posts/{postId:guid}", Get);
        group.MapDelete("/posts/{postId:guid}", Delete);
    }

    /// <summary>Un'immagine JPEG (campo <c>file</c>, al massimo 10 MB), da citare poi in un post con il suo id.</summary>
    private static async Task<IResult> UploadMedia(HttpRequest request, CurrentApiKey key, FlarelyticsDbContext db, SocialMediaStorage storage,
        MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialMediaResponse.From(await SocialPostEndpoints.SaveMediaAsync(request, key.TenantId, key.CreatedByUserId, db, storage, ct), signer));

    /// <summary>
    /// Un post nella coda. Con un <c>externalRef</c> già visto risponde 200 con
    /// il post che c'è invece di crearne un altro: si può riprovare senza paura.
    /// </summary>
    private static async Task<IResult> Create(PublicPostRequest req, CurrentApiKey key, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        if (req.ExternalRef is { Length: > 0 } reference
            && await Load(db).SingleOrDefaultAsync(p => p.ExternalRef == reference.Trim(), ct) is { } existing)
            return Results.Ok(SocialPostResponse.From(existing, signer));

        if (req.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
            throw ApiProblem.NotFound("Progetto");

        var mediaIds = req.Media?.Select(m => m.Id).ToList() ?? [];
        var media = await db.Set<SocialMedia>().Where(m => mediaIds.Contains(m.Id) && m.PostId == null).ToListAsync(ct);
        if (media.Count != mediaIds.Distinct().Count()) throw ApiProblem.BadRequest("media_not_found", "Un'immagine non esiste o è già usata in un altro post: caricala con POST /public/media.");

        var post = SocialPost.FromApi(key.TenantId, key.KeyId, key.CreatedByUserId, req.Text ?? "", req.SuggestedAtUtc, req.ExternalRef, req.ProjectId, DateTime.UtcNow);
        db.Add(post);
        foreach (var (item, position) in (req.Media ?? []).Select((m, i) => (m, i)))
        {
            var m = media.Single(x => x.Id == item.Id);
            m.AttachTo(post.Id, position, item.AltText);
            post.AddMedia(m);
        }

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/public/posts/{post.Id}", SocialPostResponse.From(post, signer));
    }

    /// <summary>Lo stato del post: in coda, programmato, pubblicato (con i link), non riuscito (con il motivo).</summary>
    private static async Task<IResult> Get(Guid postId, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialPostResponse.From(await Load(db).SingleOrDefaultAsync(p => p.Id == postId, ct) ?? throw ApiProblem.NotFound("Post"), signer));

    /// <summary>Si ritira solo finché è in coda: quando qualcuno l'ha programmato, decide il pannello.</summary>
    private static async Task<IResult> Delete(Guid postId, FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        var post = await Load(db).SingleOrDefaultAsync(p => p.Id == postId, ct) ?? throw ApiProblem.NotFound("Post");
        if (!post.IsInbox) throw ApiProblem.Conflict("post_scheduled", "Il post è già stato programmato dal pannello: non si ritira più dall'API.");

        var media = post.Media.ToList();
        db.Remove(post);
        await db.SaveChangesAsync(ct);
        foreach (var m in media) storage.Delete(m.TenantId, m.Id);
        return Results.NoContent();
    }

    private static IQueryable<SocialPost> Load(FlarelyticsDbContext db) =>
        db.Set<SocialPost>().Include(p => p.Targets).Include(p => p.Media).Where(p => !p.IsImported);
}

public record PublicPostMedia(Guid Id, string? AltText);

/// <param name="SuggestedAtUtc">La data in cui si vorrebbe pubblicarlo, facoltativa: chi lo programma la può tenere o cambiare.</param>
/// <param name="ExternalRef">Il tuo id del post (per esempio quello nel CMS): rimandarlo non crea doppioni.</param>
public record PublicPostRequest(string? Text, DateTime? SuggestedAtUtc, IReadOnlyList<PublicPostMedia>? Media, string? ExternalRef, Guid? ProjectId);

public class PublicPostRequestValidator : AbstractValidator<PublicPostRequest>
{
    public PublicPostRequestValidator()
    {
        RuleFor(x => x.Text).MaximumLength(10000);
        RuleFor(x => x.Media).Must(m => m is null || m.Count <= 10).WithMessage("Al massimo 10 immagini.");
        RuleForEach(x => x.Media).ChildRules(m => m.RuleFor(x => x.AltText).MaximumLength(1500));
        RuleFor(x => x.ExternalRef).MaximumLength(200);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Text) || x.Media is { Count: > 0 })
            .WithName("text").WithMessage("Serve un testo o almeno un'immagine.");
    }
}
