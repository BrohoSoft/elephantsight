using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Social;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http.Features;
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
    /// <summary>Post per chiamata nell'invio a blocchi.</summary>
    public const int MaxBatchPosts = 50;

    /// <summary>Tutta la richiesta a blocchi: 50 caroselli da 10 foto non ci stanno, ma un piano editoriale sì.</summary>
    public const long MaxBatchBytes = 300L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapPublicApi(this IEndpointRouteBuilder api)
    {
        var group = api.MapApiKeyGroup("");
        group.MapPost("/media", UploadMedia).DisableAntiforgery();
        group.MapPost("/posts", Create).Validating<PublicPostRequest>();
        group.MapPost("/posts/batch", CreateBatch).DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: MaxBatchBytes, valueLengthLimit: 8 * 1024 * 1024);
        group.MapGet("/posts/{postId:guid}", Get);
        group.MapDelete("/posts/{postId:guid}", Delete);
    }

    /// <summary>Un'immagine JPEG (campo <c>file</c>, al massimo 10 MB), da citare poi in un post con il suo id.</summary>
    private static async Task<IResult> UploadMedia(HttpRequest request, CurrentApiKey key, FlarelyticsDbContext db, SocialMediaStorage storage,
        MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialMediaResponse.From(await SocialPostEndpoints.SaveMediaAsync(request, key.TenantId, key.CreatedByUserId, db, storage, ct), signer));

    /// <summary>
    /// Un post nella coda, in JSON, con immagini già caricate (<c>media[].id</c>).
    /// Con un <c>externalRef</c> già visto risponde 200 con il post che c'è
    /// invece di crearne un altro: si può riprovare senza paura.
    /// </summary>
    private static async Task<IResult> Create(PublicPostRequest req, CurrentApiKey key, FlarelyticsDbContext db, SocialMediaStorage storage,
        MediaUrlSigner signer, CancellationToken ct)
    {
        var (post, existing) = await CreateOneAsync(req, files: null, key, db, storage, ct);
        return existing
            ? Results.Ok(SocialPostResponse.From(post, signer))
            : Results.Created($"/api/v1/public/posts/{post.Id}", SocialPostResponse.From(post, signer));
    }

    /// <summary>
    /// Più post in una chiamata, ciascuno con le sue immagini: multipart con il
    /// campo <c>posts</c> (un array JSON di post) e i file delle immagini, che i
    /// post citano per nome (<c>media[].file</c> = nome del campo del file).
    /// </summary>
    /// <remarks>
    /// Ogni post si controlla e si salva da solo: quelli giusti entrano in coda,
    /// per gli altri la risposta dice il motivo, e nessuno resta a metà (né il
    /// post né le sue immagini). Si può rimandare tutto il blocco: con
    /// <c>externalRef</c> i post già entrati risultano <c>existing</c>.
    /// </remarks>
    private static async Task<IResult> CreateBatch(HttpRequest request, CurrentApiKey key, FlarelyticsDbContext db, SocialMediaStorage storage,
        MediaUrlSigner signer, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBatchBytes;
        if (!request.HasFormContentType)
            throw ApiProblem.BadRequest("multipart", "Manda i post come multipart/form-data: il campo posts con il JSON e i file delle immagini.");

        var form = await request.ReadFormAsync(ct);
        if (form["posts"].ToString() is not { Length: > 0 } json)
            throw ApiProblem.BadRequest("posts_missing", "Manca il campo posts, con l'array JSON dei post.");

        List<PublicPostRequest> posts;
        try
        {
            posts = JsonSerializer.Deserialize<List<PublicPostRequest>>(json, Json) ?? [];
        }
        catch (JsonException e)
        {
            throw ApiProblem.BadRequest("posts_json", $"Il campo posts non è un array JSON di post valido: {e.Message}");
        }
        if (posts.Count is 0 or > MaxBatchPosts)
            throw ApiProblem.BadRequest("posts_count", $"Da 1 a {MaxBatchPosts} post per chiamata.");

        var results = new List<BatchPostResult>();
        foreach (var (item, index) in posts.Select((p, i) => (p, i)))
        {
            try
            {
                var (post, existing) = await CreateOneAsync(item, form.Files, key, db, storage, ct);
                results.Add(new BatchPostResult(index, item.ExternalRef, existing ? "existing" : "created", SocialPostResponse.From(post, signer), null));
            }
            catch (ApiProblem problem)
            {
                results.Add(new BatchPostResult(index, item.ExternalRef, "rejected", null, problem.Message));
            }
            catch (DbUpdateException)
            {
                // Lo stesso externalRef mandato in contemporanea da un'altra richiesta.
                results.Add(new BatchPostResult(index, item.ExternalRef, "rejected", null, "Esiste già un post con questo externalRef."));
            }
        }

        return Results.Ok(new BatchResponse(results.Count(r => r.Outcome == "created"), results.Count(r => r.Outcome == "existing"),
            results.Count(r => r.Outcome == "rejected"), results));
    }

    /// <summary>
    /// Controlla e salva un post: prima tutto il controllo (tipo, immagini,
    /// progetto), poi le scritture. Se il salvataggio fallisce, i file appena
    /// scritti si cancellano e il contesto si svuota, così il post dopo parte pulito.
    /// </summary>
    private static async Task<(SocialPost Post, bool Existing)> CreateOneAsync(PublicPostRequest req, IFormFileCollection? files, CurrentApiKey key,
        FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        if (req.ExternalRef is { Length: > 0 } reference
            && await Load(db).SingleOrDefaultAsync(p => p.ExternalRef == reference.Trim(), ct) is { } found)
            return (found, true);

        var problems = new PublicPostRequestValidator().Validate(req);
        if (!problems.IsValid) throw ApiProblem.BadRequest("post_invalid", string.Join(" ", problems.Errors.Select(e => e.ErrorMessage)));

        var items = req.Media ?? [];
        CheckType(req.Type, items.Count, !string.IsNullOrWhiteSpace(req.Text));

        if (req.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
            throw ApiProblem.NotFound("Progetto");

        // Le immagini: già caricate (id) o in questa stessa richiesta (file).
        var ids = items.Where(m => m.Id is not null).Select(m => m.Id!.Value).ToList();
        var uploaded = await db.Set<SocialMedia>().Where(m => ids.Contains(m.Id) && m.PostId == null).ToListAsync(ct);
        if (uploaded.Count != ids.Distinct().Count())
            throw ApiProblem.BadRequest("media_not_found", "Un'immagine non esiste o è già usata in un altro post: caricala con POST /public/media.");

        var fresh = new List<(int Position, byte[] Content, string FileName, int Width, int Height, string? Alt)>();
        foreach (var (item, position) in items.Select((m, i) => (m, i)))
        {
            if (item.Id is not null) continue;
            if (string.IsNullOrWhiteSpace(item.File))
                throw ApiProblem.BadRequest("media_invalid", $"L'immagine {position + 1} non ha né id né file.");
            var file = files?.GetFile(item.File)
                ?? throw ApiProblem.BadRequest("media_file_missing", $"Nella richiesta non c'è il file '{item.File}' citato dall'immagine {position + 1}.");
            if (file.Length is 0 or > SocialPostEndpoints.MaxImageBytes)
                throw ApiProblem.BadRequest("file_size", $"Il file '{item.File}' deve pesare meno di 10 MB.");

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();
            if (!JpegInfo.TryReadSize(content, out var width, out var height))
                throw ApiProblem.BadRequest("file_type", $"Il file '{item.File}' non è un JPEG (Instagram accetta solo quelli).");
            fresh.Add((position, content, Path.GetFileName(file.FileName), width, height, item.AltText));
        }

        // Da qui si scrive.
        var post = SocialPost.FromApi(key.TenantId, key.KeyId, key.CreatedByUserId, req.Text ?? "", req.SuggestedAtUtc, req.ExternalRef, req.ProjectId, DateTime.UtcNow);
        db.Add(post);
        var written = new List<SocialMedia>();
        try
        {
            foreach (var (item, position) in items.Select((m, i) => (m, i)))
            {
                SocialMedia media;
                if (item.Id is { } id)
                {
                    media = uploaded.Single(m => m.Id == id);
                }
                else
                {
                    var f = fresh.Single(x => x.Position == position);
                    media = SocialMedia.Create(key.TenantId, f.FileName, f.Content.Length, f.Width, f.Height, key.CreatedByUserId);
                    await storage.WriteAsync(key.TenantId, media.Id, f.Content, ct);
                    written.Add(media);
                    db.Add(media);
                }
                media.AttachTo(post.Id, position, item.AltText);
                post.AddMedia(media);
            }

            await db.SaveChangesAsync(ct);
            return (post, false);
        }
        catch
        {
            foreach (var m in written) storage.Delete(m.TenantId, m.Id);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Il tipo dichiarato deve tornare con le immagini: un carosello con una foto sola è quasi sempre un errore di chi lo manda.</summary>
    private static void CheckType(string? type, int images, bool hasText)
    {
        var (ok, expected) = type?.ToLowerInvariant() switch
        {
            null or "" => (true, ""),
            "text" => (images == 0 && hasText, "un post di tipo text ha un testo e nessuna immagine"),
            "image" => (images == 1, "un post di tipo image ha esattamente un'immagine"),
            "carousel" => (images is >= 2 and <= 10, "un post di tipo carousel ha da 2 a 10 immagini"),
            _ => (false, "il tipo è text, image o carousel")
        };
        if (!ok) throw ApiProblem.BadRequest("post_type", $"Tipo e immagini non tornano: {expected} (immagini ricevute: {images}).");
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

/// <summary>Un'immagine di un post: già caricata (<paramref name="Id"/>) o un file della stessa richiesta a blocchi (<paramref name="File"/>, il nome del campo).</summary>
public record PublicPostMedia(Guid? Id, string? File, string? AltText);

/// <param name="Type"><c>text</c>, <c>image</c> o <c>carousel</c>; facoltativo, ma se c'è deve tornare con le immagini.</param>
/// <param name="SuggestedAtUtc">La data in cui va pubblicato, facoltativa: chi lo programma la può tenere o cambiare.</param>
/// <param name="ExternalRef">Il tuo id del post (per esempio quello nel CMS): rimandarlo non crea doppioni.</param>
public record PublicPostRequest(string? Type, string? Text, DateTime? SuggestedAtUtc, IReadOnlyList<PublicPostMedia>? Media, string? ExternalRef, Guid? ProjectId);

/// <param name="Index">La posizione del post nell'array mandato.</param>
/// <param name="Outcome"><c>created</c>, <c>existing</c> (stesso externalRef già entrato) o <c>rejected</c> (con <paramref name="Error"/>).</param>
public record BatchPostResult(int Index, string? ExternalRef, string Outcome, SocialPostResponse? Post, string? Error);

public record BatchResponse(int Created, int Existing, int Rejected, IReadOnlyList<BatchPostResult> Results);

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
