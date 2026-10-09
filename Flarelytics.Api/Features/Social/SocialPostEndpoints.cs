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
/// Il calendario dei post. Rotte sotto <c>/orgs/{orgId}/social/posts</c> e
/// <c>/orgs/{orgId}/social/media</c>; le immagini si leggono da
/// <c>/social/media/{id}.jpg</c> con un indirizzo firmato.
/// </summary>
/// <remarks>
/// Un post si salva con l'elenco degli account su cui va: la pubblicazione la
/// fa il <see cref="SocialPublishWorker"/> all'ora indicata. Un post non in
/// bozza si controlla subito contro i limiti di ogni rete, così l'errore si
/// vede mentre lo si scrive e non a mezzanotte.
/// </remarks>
public static class SocialPostEndpoints
{

    public static void MapSocialPosts(this IEndpointRouteBuilder api)
    {
        var social = api.MapOrgGroup("/social");
        social.MapGet("/posts", List);
        social.MapGet("/posts/{postId:guid}", Get);
        social.MapGet("/inbox", Inbox);

        var admin = social.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapPost("/posts", Create).Validating<SavePostRequest>();
        admin.MapPut("/posts/{postId:guid}", Update).Validating<SavePostRequest>();
        admin.MapDelete("/posts/{postId:guid}", Delete);
        admin.MapPost("/posts/{postId:guid}/retry", Retry);
        admin.MapPost("/media", UploadMedia).DisableAntiforgery().WithFormOptions(multipartBodyLengthLimit: SocialMediaFiles.MaxSingleRequestBytes);
        admin.MapChunkedUploads(http => (http.RequestServices.GetRequiredService<CurrentOrg>().TenantId, http.User.UserId()));
        admin.MapPost("/inbox/assign", Assign).Validating<AssignInboxRequest>();
        admin.MapPost("/posts/accounts", ChangeAccounts).Validating<ChangeAccountsRequest>();

        // Anonima: la usa Instagram, che scarica l'immagine da sé, e i tag
        // <img> del pannello, che non possono mandare l'access token. La firma
        // nell'indirizzo è il permesso.
        api.MapGet("/social/media/{file}", ServeMedia).AllowAnonymous();
    }

    /// <summary>I post fra due istanti (il mese che il calendario sta mostrando), con i loro esiti.</summary>
    private static async Task<IResult> List(DateTime from, DateTime to, Guid? projectId, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(100)) throw ApiProblem.BadRequest("range", "Un periodo di al massimo 100 giorni.");
        var (start, end) = (DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc), DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc));

        var query = db.Set<SocialPost>().AsNoTracking().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => !p.IsInbox && p.ScheduledAtUtc >= start && p.ScheduledAtUtc < end);
        if (projectId is { } pid) query = query.Where(p => p.ProjectId == pid);

        var posts = await query.OrderBy(p => p.ScheduledAtUtc).ToListAsync(ct);
        return Results.Ok(posts.Select(p => SocialPostResponse.From(p, signer)));
    }

    private static async Task<IResult> Get(Guid postId, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialPostResponse.From(await LoadAsync(db, postId, ct), signer));

    private static async Task<IResult> Create(SavePostRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        MediaUrlSigner signer, SocialMediaStorage storage, CancellationToken ct)
    {
        var post = SocialPost.Create(org.TenantId, principal.UserId());
        db.Add(post);
        await ApplyAsync(post, req, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{org.TenantId}/social/posts/{post.Id}", SocialPostResponse.From(post, signer));
    }

    private static async Task<IResult> Update(Guid postId, SavePostRequest req, FlarelyticsDbContext db, MediaUrlSigner signer, SocialMediaStorage storage, CancellationToken ct)
    {
        var post = await LoadAsync(db, postId, ct);
        if (post.IsImported)
            throw ApiProblem.Conflict("post_imported", "È un post pubblicato fuori da WatchStore: qui si legge e basta.");
        if (!post.IsEditable)
            throw ApiProblem.Conflict("post_published", "Il post è già uscito (o sta uscendo) su almeno un account: non si modifica più.");

        await ApplyAsync(post, req, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialPostResponse.From(post, signer));
    }

    /// <summary>
    /// Toglie il post dal calendario. Se è già uscito su qualche rete lì resta:
    /// si cancella solo da qui.
    /// </summary>
    private static async Task<IResult> Delete(Guid postId, FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        var post = await LoadAsync(db, postId, ct);
        // Un post importato tornerebbe al giro dopo: è una copia di quello sulla rete.
        if (post.IsImported)
            throw ApiProblem.Conflict("post_imported", "È un post pubblicato fuori da WatchStore: si toglie cancellandolo sulla rete.");
        if (post.Targets.Any(t => t.Status == SocialTargetStatus.Publishing))
            throw ApiProblem.Conflict("post_publishing", "Il post si sta pubblicando proprio adesso: riprova fra un momento.");

        var media = post.Media.ToList();
        db.Remove(post);
        await db.SaveChangesAsync(ct);
        foreach (var m in media) storage.Delete(m);
        return Results.NoContent();
    }

    /// <summary>Rimette in coda gli account falliti, per pubblicarli al prossimo giro.</summary>
    private static async Task<IResult> Retry(Guid postId, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var post = await LoadAsync(db, postId, ct);
        var failed = post.Targets.Where(t => t.Status == SocialTargetStatus.Failed).ToList();
        if (failed.Count == 0) throw ApiProblem.BadRequest("nothing_to_retry", "Nessun account da riprovare.");

        foreach (var t in failed) t.Requeue();
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialPostResponse.From(post, signer));
    }

    /// <summary>
    /// Un'immagine, già in JPEG: la conversione la fa il pannello, così il
    /// server non ha bisogno di librerie grafiche. Qui si controlla che sia
    /// davvero un JPEG e se ne leggono le dimensioni.
    /// </summary>
    private static async Task<IResult> UploadMedia(HttpRequest request, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        SocialMediaStorage storage, MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialMediaResponse.From(await SaveMediaAsync(request, org.TenantId, principal.UserId(), db, storage, ct), signer));

    /// <summary>
    /// Il campo <c>file</c> del multipart (un JPEG, o un video MP4/MOV fino a
    /// 100 MB; più grandi a pezzi), salvato come media non ancora attaccato a un
    /// post. Lo usa anche l'API pubblica.
    /// </summary>
    public static async Task<SocialMedia> SaveMediaAsync(HttpRequest request, Guid tenantId, Guid userId, FlarelyticsDbContext db,
        SocialMediaStorage storage, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = SocialMediaFiles.MaxSingleRequestBytes;
        if (!request.HasFormContentType) throw ApiProblem.BadRequest("file_missing", "Manda il file come multipart/form-data, nel campo file.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? throw ApiProblem.BadRequest("file_missing", "Manca il file (campo file).");

        var media = await SocialMediaFiles.FromFormFileAsync(file, tenantId, userId, storage, ct);
        db.Add(media);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            storage.Delete(media);
            throw;
        }
        return media;
    }

    /// <summary>
    /// La coda "Da programmare": i post arrivati con una chiave API e non
    /// ancora assegnati. Dalla data proposta più vicina; quelli senza data in fondo.
    /// </summary>
    private static async Task<IResult> Inbox(CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var posts = await db.Set<SocialPost>().AsNoTracking().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => p.IsInbox)
            .OrderBy(p => p.SuggestedAtUtc == null).ThenBy(p => p.SuggestedAtUtc).ThenBy(p => p.CreatedAtUtc)
            .ToListAsync(ct);

        // Le chiavi non sono del tenant (vedi ApiKey): il filtro sul tenant va messo a mano.
        var keyIds = posts.Where(p => p.ApiKeyId != null).Select(p => p.ApiKeyId!.Value).Distinct().ToList();
        var keyNames = await db.Set<ApiKey>().Where(k => k.TenantId == org.TenantId && keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.Name, ct);

        return Results.Ok(posts.Select(p => SocialPostResponse.From(p, signer) with
        {
            Source = p.ApiKeyId is { } id ? keyNames.GetValueOrDefault(id) : null
        }));
    }

    /// <summary>
    /// Assegna più post della coda agli stessi account, all'ora indicata o a
    /// quella proposta da ciascuno. Ogni post si controlla da solo: quelli che
    /// non vanno bene per una rete restano in coda con il motivo, gli altri
    /// diventano programmati.
    /// </summary>
    private static async Task<IResult> Assign(AssignInboxRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var accounts = await db.Set<SocialAccount>().Where(a => req.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != req.AccountIds.Distinct().Count()) throw ApiProblem.NotFound("Account");

        var posts = await db.Set<SocialPost>().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => p.IsInbox && req.PostIds.Contains(p.Id)).ToListAsync(ct);

        var results = new List<AssignResult>();
        foreach (var id in req.PostIds.Distinct())
        {
            var post = posts.SingleOrDefault(p => p.Id == id);
            if (post is null)
            {
                results.Add(new AssignResult(id, false, "Non è più nella coda."));
                continue;
            }

            var when = req.ScheduledAtUtc ?? post.SuggestedAtUtc;
            if (when is null)
            {
                results.Add(new AssignResult(id, false, "Non ha una data proposta: scegline una."));
                continue;
            }

            // Le scelte per TikTok arrivano dall'assegnazione (le fa la persona,
            // qui); la griglia di Instagram resta quella del post, se non cambiata.
            if (req.Options is { } chosen)
                post.SetOptions(chosen with { InstagramShowInGrid = req.Options.InstagramShowInGrid && post.Options.InstagramShowInGrid });

            var problems = ProblemsFor(post, accounts, _ => null);
            if (problems.Count > 0)
            {
                results.Add(new AssignResult(id, false, string.Join(" ", problems)));
                continue;
            }

            post.Update(post.Text, when.Value, isDraft: false, post.ProjectId);
            foreach (var a in accounts.Where(a => post.Targets.All(t => t.AccountId != a.Id)))
            {
                var target = SocialPostTarget.For(post, a);
                post.AddTarget(target);
                db.Add(target);
            }
            post.LeaveInbox();
            results.Add(new AssignResult(id, true, null));
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(results);
    }

    private static IResult ServeMedia(string file, long? e, string? s, MediaUrlSigner signer, SocialMediaStorage storage)
    {
        var extension = Path.GetExtension(file);
        var contentType = extension switch { ".jpg" => "image/jpeg", ".mp4" => "video/mp4", ".mov" => "video/quicktime", _ => null };
        if (contentType is null || e is null || !signer.TryVerify(file[..^extension.Length], e.Value, s, DateTime.UtcNow, out var tenantId, out var mediaId))
            return Results.NotFound();

        var path = storage.PathFor(tenantId, mediaId, extension);
        if (!File.Exists(path)) return Results.NotFound();
        // I video si leggono a pezzi (il player del pannello, e chi scarica per la rete).
        return Results.File(path, contentType, enableRangeProcessing: contentType != "image/jpeg");
    }

    /// <summary>
    /// Aggiunge e/o toglie account a più post insieme: quelli scelti, o tutti
    /// quelli ancora da uscire. Ogni post si controlla da solo, con gli account
    /// che avrebbe dopo il cambio: quelli che non andrebbero bene (una foto per
    /// TikTok, un testo troppo lungo per Bluesky…) restano com'erano, con il motivo.
    /// </summary>
    /// <remarks>
    /// Valgono le regole dell'editor: si cambiano solo post non ancora usciti da
    /// nessuna parte, e un post programmato non resta senza account. I testi
    /// diversi per account restano a quelli che li avevano.
    /// </remarks>
    private static async Task<IResult> ChangeAccounts(ChangeAccountsRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var add = req.AddAccountIds ?? [];
        var remove = req.RemoveAccountIds ?? [];
        var accounts = await db.Set<SocialAccount>().ToListAsync(ct);
        if (add.Concat(remove).Any(id => accounts.All(a => a.Id != id))) throw ApiProblem.NotFound("Account");

        var now = DateTime.UtcNow;
        var query = db.Set<SocialPost>().Include(p => p.Targets).Include(p => p.Media).Where(p => !p.IsImported && !p.IsInbox);
        query = req.AllUpcoming
            ? query.Where(p => p.ScheduledAtUtc >= now)
            : query.Where(p => (req.PostIds ?? new List<Guid>()).Contains(p.Id));
        var posts = await query.OrderBy(p => p.ScheduledAtUtc).ToListAsync(ct);

        var results = new List<ChangeAccountsResult>();
        foreach (var post in posts)
        {
            var current = post.Targets.Where(t => t.AccountId != null).Select(t => t.AccountId!.Value).ToHashSet();
            var next = current.Union(add).Except(remove).ToHashSet();
            if (next.SetEquals(current))
            {
                results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, false, null));
                continue;
            }
            if (!post.IsEditable)
            {
                results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, false, "È già uscito (o sta uscendo) su almeno un account."));
                continue;
            }
            if (!post.IsDraft && next.Count == 0)
            {
                results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, false, "Resterebbe senza account: cancellalo o mettilo in bozza."));
                continue;
            }

            // Le scelte per TikTok arrivano con la richiesta (le fa la persona,
            // qui); il resto delle opzioni del post non cambia.
            var options = post.Options;
            if (req.Options is { } chosen && add.Any(id => accounts.Single(a => a.Id == id).Network == SocialNetwork.TikTok))
                options = chosen with { InstagramShowInGrid = options.InstagramShowInGrid };

            if (!post.IsDraft)
            {
                var problems = ProblemsFor(post, accounts.Where(a => next.Contains(a.Id)),
                    a => post.Targets.FirstOrDefault(t => t.AccountId == a.Id)?.TextOverride, options);
                if (problems.Count > 0)
                {
                    results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, false, string.Join(" ", problems)));
                    continue;
                }
            }

            foreach (var t in post.Targets.Where(t => t.AccountId is { } id && !next.Contains(id)).ToList())
            {
                post.RemoveTarget(t);
                db.Remove(t);
            }
            foreach (var a in accounts.Where(a => next.Contains(a.Id) && !current.Contains(a.Id)))
            {
                var target = SocialPostTarget.For(post, a);
                post.AddTarget(target);
                db.Add(target);
            }
            post.SetOptions(options);
            results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, true, null));
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(results);
    }

    /// <summary>Scrive sul post testo, data, account e immagini della richiesta, e lo controlla contro i limiti delle reti.</summary>
    private static async Task ApplyAsync(SocialPost post, SavePostRequest req, FlarelyticsDbContext db, SocialMediaStorage storage, CancellationToken ct)
    {
        if (req.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
            throw ApiProblem.NotFound("Progetto");

        post.Update(req.Text, req.ScheduledAtUtc, req.IsDraft, req.ProjectId);
        if (req.Options is { } options) post.SetOptions(options);
        // Un post della coda programmato dal pannello diventa un post qualsiasi;
        // salvato come bozza resta in coda.
        if (!req.IsDraft) post.LeaveInbox();

        // Gli account: si tolgono quelli non più scelti, si aggiungono i nuovi.
        var accounts = await db.Set<SocialAccount>().Where(a => req.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != req.AccountIds.Distinct().Count()) throw ApiProblem.NotFound("Account");

        foreach (var t in post.Targets.Where(t => t.AccountId is not { } id || !req.AccountIds.Contains(id)).ToList())
        {
            post.RemoveTarget(t);
            db.Remove(t);
        }
        foreach (var a in accounts.Where(a => post.Targets.All(t => t.AccountId != a.Id)))
        {
            var target = SocialPostTarget.For(post, a);
            post.AddTarget(target);
            db.Add(target);
        }
        foreach (var t in post.Targets)
        {
            t.SetTextOverride(req.Overrides?.GetValueOrDefault(t.AccountId!.Value));
            // Salvare un post fallito lo rimette in coda: è la correzione dopo l'errore.
            if (t.Status == SocialTargetStatus.Failed) t.Requeue();
        }

        // Le immagini: nell'ordine della richiesta. Quelle tolte si cancellano.
        var mediaIds = req.Media.Select(m => m.Id).ToList();
        var media = await db.Set<SocialMedia>().Where(m => mediaIds.Contains(m.Id) && (m.PostId == null || m.PostId == post.Id)).ToListAsync(ct);
        if (media.Count != mediaIds.Distinct().Count()) throw ApiProblem.NotFound("Immagine");

        foreach (var m in post.Media.Where(m => !mediaIds.Contains(m.Id)).ToList())
        {
            post.RemoveMedia(m);
            db.Remove(m);
            storage.Delete(m);
        }
        foreach (var (item, position) in req.Media.Select((m, i) => (m, i)))
        {
            var m = media.Single(x => x.Id == item.Id);
            m.AttachTo(post.Id, position, item.AltText);
            if (!post.Media.Contains(m)) post.AddMedia(m);
        }

        if (post.IsDraft) return;

        if (post.Targets.Count == 0) throw ApiProblem.BadRequest("no_accounts", "Scegli almeno un account su cui pubblicare.");
        var problems = ProblemsFor(post, accounts, a => post.Targets.Single(t => t.AccountId == a.Id).TextOverride);
        if (problems.Count > 0) throw ApiProblem.BadRequest("post_invalid", string.Join(" ", problems));
    }

    /// <summary>Quello che impedisce di pubblicare il post su ciascun account, in frasi da mostrare.</summary>
    private static List<string> ProblemsFor(SocialPost post, IEnumerable<SocialAccount> accounts, Func<SocialAccount, string?> textOverride,
        PostOptions? options = null)
    {
        var ordered = post.Media.OrderBy(m => m.Position).ToList();
        options ??= post.Options;
        return accounts
            .Select(a => (Account: a, Problems: SocialRules.Problems(textOverride(a) ?? post.Text, ordered, SocialRules.For(a))
                .Concat(SocialRules.OptionProblems(a.Network, options)).ToList()))
            .Where(x => x.Problems.Count > 0)
            .Select(x => $"{NetworkName(x.Account.Network)} ({x.Account.Handle ?? x.Account.Name}): {string.Join("; ", x.Problems)}.")
            .ToList();
    }

    private static string NetworkName(SocialNetwork n) => n switch
    {
        SocialNetwork.FacebookPage => "Facebook",
        _ => n.ToString()
    };

    private static async Task<SocialPost> LoadAsync(FlarelyticsDbContext db, Guid postId, CancellationToken ct) =>
        await db.Set<SocialPost>().Include(p => p.Targets).Include(p => p.Media).SingleOrDefaultAsync(p => p.Id == postId, ct)
        ?? throw ApiProblem.NotFound("Post");
}

public enum SocialPostStatus { Draft, Scheduled, Publishing, Published, PartiallyFailed, Failed, Inbox }

/// <param name="Imported">Pubblicato fuori da WatchStore e copiato qui: si legge e basta.</param>
/// <param name="Inbox">Arrivato con una chiave API e in attesa nella coda "Da programmare".</param>
/// <param name="SuggestedAtUtc">La data proposta da chi l'ha mandato.</param>
/// <param name="Source">Il nome della chiave API da cui è arrivato (solo nell'elenco della coda).</param>
public record SocialPostResponse(Guid Id, string Text, DateTime ScheduledAtUtc, bool IsDraft, Guid? ProjectId, SocialPostStatus Status, bool Editable, bool Imported,
    bool Inbox, DateTime? SuggestedAtUtc, string? ExternalRef, PostOptions Options, IReadOnlyList<SocialMediaResponse> Media, IReadOnlyList<SocialTargetResponse> Targets, DateTime CreatedAtUtc)
{
    public string? Source { get; init; }

    public static SocialPostResponse From(SocialPost p, MediaUrlSigner signer) => new(
        p.Id, p.Text, p.ScheduledAtUtc, p.IsDraft, p.ProjectId, StatusOf(p), p.IsEditable, p.IsImported,
        p.IsInbox, p.SuggestedAtUtc, p.ExternalRef, p.Options,
        p.Media.OrderBy(m => m.Position).Select(m => SocialMediaResponse.From(m, signer)).ToList(),
        p.Targets.OrderBy(t => t.Network).ThenBy(t => t.AccountName).Select(SocialTargetResponse.From).ToList(),
        p.CreatedAtUtc);

    /// <summary>Uno stato solo per il calendario, dagli esiti dei singoli account.</summary>
    private static SocialPostStatus StatusOf(SocialPost p)
    {
        var t = p.Targets;
        if (p.IsInbox) return SocialPostStatus.Inbox;
        if (p.IsDraft) return SocialPostStatus.Draft;
        if (t.Count > 0 && t.All(x => x.Status == SocialTargetStatus.Published)) return SocialPostStatus.Published;
        if (t.Any(x => x.Status == SocialTargetStatus.Publishing)) return SocialPostStatus.Publishing;
        if (t.Any(x => x.Status == SocialTargetStatus.Failed))
            return t.Any(x => x.Status == SocialTargetStatus.Published) ? SocialPostStatus.PartiallyFailed : SocialPostStatus.Failed;
        return t.Any(x => x.Status == SocialTargetStatus.Published) ? SocialPostStatus.Publishing : SocialPostStatus.Scheduled;
    }
}

public record SocialTargetResponse(Guid Id, Guid? AccountId, SocialNetwork Network, string AccountName, string? TextOverride,
    SocialTargetStatus Status, string? ExternalUrl, string? Error, DateTime? NextAttemptAtUtc, DateTime? PublishedAtUtc)
{
    public static SocialTargetResponse From(SocialPostTarget t) =>
        new(t.Id, t.AccountId, t.Network, t.AccountName, t.TextOverride, t.Status, t.ExternalUrl, t.Error, t.NextAttemptAtUtc, t.PublishedAtUtc);
}

/// <param name="Url">Firmato: vale per le anteprime del pannello finché la pagina resta aperta un pomeriggio.</param>
public record SocialMediaResponse(Guid Id, MediaKind Kind, int Width, int Height, long SizeBytes, int? DurationMs, bool FastStart, string? AltText, string Url)
{
    public static readonly TimeSpan PreviewValidity = TimeSpan.FromHours(12);

    public static SocialMediaResponse From(SocialMedia m, MediaUrlSigner signer) =>
        new(m.Id, m.Kind, m.Width, m.Height, m.SizeBytes, m.DurationMs, m.FastStart, m.AltText, signer.PathFor(m, DateTime.UtcNow.Add(PreviewValidity)));
}

public record SavePostMedia(Guid Id, string? AltText);

/// <param name="ScheduledAtUtc">Un'ora per tutti; null = ciascuno all'ora che ha proposto.</param>
/// <param name="Options">Le scelte per le reti che le chiedono (TikTok), uguali per tutti i post assegnati.</param>
public record AssignInboxRequest(IReadOnlyList<Guid> PostIds, IReadOnlyList<Guid> AccountIds, DateTime? ScheduledAtUtc, PostOptions? Options = null);

public record AssignResult(Guid PostId, bool Scheduled, string? Problem);

/// <param name="AllUpcoming">Tutti i post ancora da uscire (non solo quelli in <paramref name="PostIds"/>).</param>
/// <param name="Options">Le scelte per TikTok, quando si aggiunge un account TikTok.</param>
public record ChangeAccountsRequest(IReadOnlyList<Guid>? PostIds, bool AllUpcoming, IReadOnlyList<Guid>? AddAccountIds, IReadOnlyList<Guid>? RemoveAccountIds,
    PostOptions? Options = null);

/// <param name="Changed">Il post ha gli account nuovi. Falso con <paramref name="Problem"/> nullo: non c'era niente da cambiare.</param>
public record ChangeAccountsResult(Guid PostId, string Text, DateTime ScheduledAtUtc, bool Changed, string? Problem);

public class ChangeAccountsRequestValidator : AbstractValidator<ChangeAccountsRequest>
{
    public ChangeAccountsRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.AddAccountIds?.Count ?? 0) + (x.RemoveAccountIds?.Count ?? 0) > 0)
            .WithName("accounts").WithMessage("Scegli almeno un account da aggiungere o togliere.");
        RuleFor(x => x).Must(x => x.AllUpcoming || x.PostIds is { Count: > 0 })
            .WithName("postIds").WithMessage("Scegli i post, o tutti quelli ancora da uscire.");
        RuleFor(x => x.PostIds).Must(p => p is null || p.Count <= 500);
    }
}

public class AssignInboxRequestValidator : AbstractValidator<AssignInboxRequest>
{
    public AssignInboxRequestValidator()
    {
        RuleFor(x => x.PostIds).NotEmpty().Must(p => p.Count <= 200);
        RuleFor(x => x.AccountIds).NotEmpty().WithMessage("Scegli almeno un account.").Must(a => a.Count <= 50);
    }
}

/// <param name="Overrides">Testi diversi per account: id dell'account → testo.</param>
public record SavePostRequest(string Text, DateTime ScheduledAtUtc, bool IsDraft, Guid? ProjectId, IReadOnlyList<Guid> AccountIds,
    IReadOnlyList<SavePostMedia> Media, Dictionary<Guid, string>? Overrides, PostOptions? Options = null);

public class SavePostRequestValidator : AbstractValidator<SavePostRequest>
{
    public SavePostRequestValidator()
    {
        RuleFor(x => x.Text).NotNull().MaximumLength(10000);
        RuleFor(x => x.AccountIds).NotNull().Must(a => a.Count <= 50);
        RuleFor(x => x.Media).NotNull().Must(m => m.Count <= 10).WithMessage("Al massimo 10 immagini.");
        RuleForEach(x => x.Media).ChildRules(m => m.RuleFor(x => x.AltText).MaximumLength(1500));
        RuleFor(x => x.Overrides).Must(o => o is null || o.Values.All(v => v.Length <= 10000));
    }
}
