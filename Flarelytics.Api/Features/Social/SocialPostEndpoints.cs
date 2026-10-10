using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Core.Social.Media;
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
        var social = api.MapOrgGroup("/social").RequireSection(AppSections.Social);
        social.MapGet("/posts", List);
        social.MapGet("/posts/{postId:guid}", Get);
        social.MapGet("/inbox", Inbox);

        var admin = social.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapPost("/posts", Create).Validating<SavePostRequest>();
        admin.MapPut("/posts/{postId:guid}", Update).Validating<SavePostRequest>();
        admin.MapDelete("/posts/{postId:guid}", Delete);
        admin.MapPost("/posts/{postId:guid}/retry", Retry);
        admin.MapPost("/media", UploadMedia).DisableAntiforgery().WithFormOptions(multipartBodyLengthLimit: SocialMediaFiles.MaxSingleRequestBytes);
        admin.MapPost("/media/copies", CopyMedia).Validating<CopyMediaRequest>();
        admin.MapPost("/media/{mediaId:guid}/thumbnail", UploadThumbnail);
        admin.MapChunkedUploads(http => (http.RequestServices.GetRequiredService<CurrentOrg>().TenantId, http.User.UserId()));
        admin.MapPost("/inbox/assign", Assign).Validating<AssignInboxRequest>();
        admin.MapPost("/posts/accounts", ChangeAccounts).Validating<ChangeAccountsRequest>();

        // Anonima: la usa Instagram, che scarica l'immagine da sé, e i tag
        // <img> del pannello, che non possono mandare l'access token. La firma
        // nell'indirizzo è il permesso. Con lo storage remoto il file passa da
        // qui decifrato: Bunny non ha mai il chiaro e il suo indirizzo non esce.
        api.MapGet("/social/media/{file}", ServeMedia).AllowAnonymous();
    }

    /// <summary>I post fra due istanti (il mese che il calendario sta mostrando), con i loro esiti.</summary>
    /// <remarks>Senza progetto: tutti quelli che il membro vede (il calendario dell'organizzazione). Con: il calendario del progetto.</remarks>
    private static async Task<IResult> List(DateTime from, DateTime to, Guid? projectId, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        if (to <= from || to - from > TimeSpan.FromDays(100)) throw ApiProblem.BadRequest("range", "Un periodo di al massimo 100 giorni.");
        var (start, end) = (DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc), DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc));
        if (projectId is not null) org.EnsureCanSee(projectId);

        var query = Visible(db, org).AsNoTracking().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => !p.IsInbox && p.ScheduledAtUtc >= start && p.ScheduledAtUtc < end);
        if (projectId is { } pid) query = query.Where(p => p.ProjectId == pid);

        var posts = await query.OrderBy(p => p.ScheduledAtUtc).ToListAsync(ct);
        return Results.Ok(posts.Select(p => SocialPostResponse.From(p, signer)));
    }

    private static async Task<IResult> Get(Guid postId, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialPostResponse.From(await LoadAsync(db, org, postId, ct), signer));

    /// <summary>
    /// I post che il membro vede: tutti, o quelli dei suoi progetti. Un post
    /// senza progetto è dell'organizzazione e lo vede solo chi vede tutto.
    /// </summary>
    public static IQueryable<SocialPost> Visible(FlarelyticsDbContext db, CurrentOrg org)
    {
        var query = db.Set<SocialPost>().AsQueryable();
        if (org.VisibleProjects is { } ids) query = query.Where(p => p.ProjectId != null && ids.Contains(p.ProjectId.Value));
        return query;
    }

    private static async Task<IResult> Create(SavePostRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        MediaUrlSigner signer, SocialMediaStore storage, CancellationToken ct)
    {
        var post = SocialPost.Create(org.TenantId, principal.UserId());
        db.Add(post);
        await ApplyAsync(post, req, org, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/orgs/{org.TenantId}/social/posts/{post.Id}", SocialPostResponse.From(post, signer));
    }

    private static async Task<IResult> Update(Guid postId, SavePostRequest req, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, SocialMediaStore storage, CancellationToken ct)
    {
        var post = await LoadAsync(db, org, postId, ct);
        if (post.IsImported)
            throw ApiProblem.Conflict("post_imported", "È un post pubblicato fuori da ElephantSight: qui si legge e basta.");
        if (!post.IsEditable)
            throw ApiProblem.Conflict("post_published", "Il post è già uscito (o sta uscendo) su almeno un account: non si modifica più.");

        await ApplyAsync(post, req, org, db, storage, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialPostResponse.From(post, signer));
    }

    /// <summary>
    /// Toglie il post dal calendario. Se è già uscito su qualche rete lì resta:
    /// si cancella solo da qui.
    /// </summary>
    private static async Task<IResult> Delete(Guid postId, CurrentOrg org, FlarelyticsDbContext db, SocialMediaStore storage, CancellationToken ct)
    {
        var post = await LoadAsync(db, org, postId, ct);
        // Un post importato tornerebbe al giro dopo: è una copia di quello sulla rete.
        if (post.IsImported)
            throw ApiProblem.Conflict("post_imported", "È un post pubblicato fuori da ElephantSight: si toglie cancellandolo sulla rete.");
        if (post.Targets.Any(t => t.Status == SocialTargetStatus.Publishing))
            throw ApiProblem.Conflict("post_publishing", "Il post si sta pubblicando proprio adesso: riprova fra un momento.");

        var media = post.Media.ToList();
        db.Remove(post);
        await db.SaveChangesAsync(ct);
        foreach (var m in media) await storage.DeleteAsync(m, CancellationToken.None);
        return Results.NoContent();
    }

    /// <summary>Rimette in coda gli account falliti, per pubblicarli al prossimo giro.</summary>
    private static async Task<IResult> Retry(Guid postId, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        var post = await LoadAsync(db, org, postId, ct);
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
        SocialMediaStore storage, MediaUrlSigner signer, CancellationToken ct) =>
        Results.Ok(SocialMediaResponse.From(await SaveMediaAsync(request, org.TenantId, principal.UserId(), db, storage, ct), signer));

    /// <summary>
    /// Il campo <c>file</c> del multipart (un JPEG, o un video MP4/MOV fino a
    /// 100 MB; più grandi a pezzi), salvato come media non ancora attaccato a un
    /// post. Lo usa anche l'API pubblica.
    /// </summary>
    public static async Task<SocialMedia> SaveMediaAsync(HttpRequest request, Guid tenantId, Guid userId, FlarelyticsDbContext db,
        SocialMediaStore storage, CancellationToken ct)
    {
        // Senza storage utilizzabile si rifiuta prima di leggere il corpo.
        _ = storage.WritableLocation;
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
            await storage.DeleteAsync(media, CancellationToken.None);
            throw;
        }
        return media;
    }

    public const int MaxThumbnailBytes = 512 * 1024;

    /// <summary>
    /// La miniatura (~400 px, JPEG) di un'immagine o di un video, fatta dal
    /// pannello: resta quando l'originale si cancella dopo la pubblicazione.
    /// Il corpo è il JPEG, senza multipart.
    /// </summary>
    /// <remarks>
    /// Si manda per un file appena caricato, oppure, per uno già nei post che
    /// il membro vede e che ancora non ce l'ha (quelli arrivati con l'API
    /// pubblica, che non manda miniature), quando il pannello lo mostra.
    /// </remarks>
    private static async Task<IResult> UploadThumbnail(Guid mediaId, HttpRequest request, CurrentOrg org, FlarelyticsDbContext db,
        SocialMediaStore storage, MediaUrlSigner signer, CancellationToken ct)
    {
        var media = await db.Set<SocialMedia>().SingleOrDefaultAsync(m => m.Id == mediaId, ct) ?? throw ApiProblem.NotFound("Immagine");
        var attached = media.PostId is not null || media.RecurringPostId is not null;
        if (attached)
        {
            var visible = media.PostId is { } postId
                ? await Visible(db, org).AnyAsync(p => p.Id == postId, ct)
                : await SocialRecurringEndpoints.Visible(db, org).AnyAsync(r => r.Id == media.RecurringPostId, ct);
            if (!visible) throw ApiProblem.NotFound("Immagine");
            if (media.HasThumbnail) return Results.Ok(SocialMediaResponse.From(media, signer));
        }

        var limited = new byte[MaxThumbnailBytes + 1];
        int read, total = 0;
        while ((read = await request.Body.ReadAsync(limited.AsMemory(total, limited.Length - total), ct)) > 0)
        {
            total += read;
            if (total > MaxThumbnailBytes) throw ApiProblem.BadRequest("file_size", "La miniatura supera i 512 KB.");
        }
        var jpeg = limited[..total];
        if (!JpegInfo.TryReadSize(jpeg, out var width, out var height) || width > 1024 || height > 1024)
            throw ApiProblem.BadRequest("file_type", "La miniatura dev'essere un JPEG fino a 1024 px per lato.");

        await storage.SaveThumbnailAsync(media, jpeg, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialMediaResponse.From(media, signer));
    }

    /// <summary>
    /// Copie libere di immagini e video già usati (da un post ricorrente da
    /// duplicare, per esempio): file nuovi, non attaccati a niente, che si
    /// attaccano al salvataggio come quelli appena caricati. Quelle mai usate le
    /// cancella il worker dopo un giorno. Nell'ordine della richiesta.
    /// </summary>
    private static async Task<IResult> CopyMedia(CopyMediaRequest req, CurrentOrg org, FlarelyticsDbContext db, SocialMediaStore storage, MediaUrlSigner signer,
        CancellationToken ct)
    {
        var sources = await db.Set<SocialMedia>().Where(m => req.Ids.Contains(m.Id)).ToListAsync(ct);
        if (sources.Count != req.Ids.Distinct().Count()) throw ApiProblem.NotFound("Immagine");

        // Si copia solo da post e post ricorrenti che il membro vede.
        if (!org.IsFull)
        {
            var postIds = sources.Where(m => m.PostId != null).Select(m => m.PostId!.Value).ToList();
            var recurringIds = sources.Where(m => m.RecurringPostId != null).Select(m => m.RecurringPostId!.Value).ToList();
            var visiblePosts = await Visible(db, org).CountAsync(p => postIds.Contains(p.Id), ct);
            var visibleRecurring = await SocialRecurringEndpoints.Visible(db, org).CountAsync(r => recurringIds.Contains(r.Id), ct);
            if (visiblePosts != postIds.Distinct().Count() || visibleRecurring != recurringIds.Distinct().Count()) throw ApiProblem.NotFound("Immagine");
        }

        if (sources.Any(m => !m.HasOriginal))
            throw ApiProblem.Conflict("media_original_deleted",
                "Il file originale di un'immagine o di un video è stato cancellato dopo la pubblicazione (ne resta l'anteprima): caricalo di nuovo.");

        var copies = new List<SocialMedia>();
        try
        {
            foreach (var (id, position) in req.Ids.Select((id, i) => (id, i)))
            {
                var source = sources.Single(m => m.Id == id);
                var copy = source.CopyFor(null, position);
                copies.Add(copy);
                await storage.CopyAsync(source, copy, ct);
            }
            db.AddRange(copies);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e)
        {
            foreach (var c in copies) await storage.DeleteAsync(c, CancellationToken.None);
            if (e is FileNotFoundException) throw ApiProblem.NotFound("Il file dell'immagine");
            throw;
        }
        return Results.Ok(copies.Select(m => SocialMediaResponse.From(m, signer)));
    }

    /// <summary>
    /// La coda "Da programmare": i post arrivati con una chiave API e non
    /// ancora assegnati. Dalla data proposta più vicina; quelli senza data in fondo.
    /// </summary>
    /// <remarks>Con <paramref name="projectId"/> la coda di un progetto; senza, quella di tutti i progetti che il membro vede.</remarks>
    private static async Task<IResult> Inbox(Guid? projectId, CurrentOrg org, FlarelyticsDbContext db, MediaUrlSigner signer, CancellationToken ct)
    {
        if (projectId is not null) org.EnsureCanSee(projectId);
        var posts = await Visible(db, org).AsNoTracking().Include(p => p.Targets).Include(p => p.Media)
            .Where(p => p.IsInbox && (projectId == null || p.ProjectId == projectId))
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
    private static async Task<IResult> Assign(AssignInboxRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        if (req.ProjectId is { } chosenProject)
        {
            org.EnsureCanSee(chosenProject);
            if (!await db.Set<Project>().AnyAsync(p => p.Id == chosenProject, ct)) throw ApiProblem.NotFound("Progetto");
        }
        var projectRequired = await HasProjectsAsync(db, ct);
        var accounts = await SocialAccountEndpoints.VisibleAccounts(db, org).Where(a => req.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != req.AccountIds.Distinct().Count()) throw ApiProblem.NotFound("Account");

        var posts = await Visible(db, org).Include(p => p.Targets).Include(p => p.Media)
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

            // Programmato, un post sta in un progetto: quello che ha già (scelto
            // nel pannello o mandato con l'API), o quello scelto qui.
            var project = post.ProjectId ?? req.ProjectId;
            if (project is null && projectRequired)
            {
                results.Add(new AssignResult(id, false, "Scegli il progetto in cui programmarlo."));
                continue;
            }
            if (post.ProjectId is null && project is not null) post.Update(post.Text, post.ScheduledAtUtc, isDraft: true, project);

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

    private static async Task<IResult> ServeMedia(string file, long? e, string? s, HttpContext http, MediaUrlSigner signer, SocialMediaStore storage,
        CancellationToken ct)
    {
        var thumbnail = file.EndsWith(MediaUrlSigner.ThumbnailSuffix, StringComparison.Ordinal);
        var extension = thumbnail ? ".jpg" : Path.GetExtension(file);
        var contentType = extension switch { ".jpg" => "image/jpeg", ".mp4" => "video/mp4", ".mov" => "video/quicktime", _ => null };
        var id = file[..^(thumbnail ? MediaUrlSigner.ThumbnailSuffix.Length : extension.Length)];
        if (contentType is null || e is null || !signer.TryVerify(id, e.Value, s, DateTime.UtcNow, out var tenantId, out var mediaId, thumbnail))
            return Results.NotFound();

        // I video si leggono a pezzi (il player del pannello, e chi scarica per
        // la rete): da Bunny si scaricano e si decifrano solo i blocchi chiesti.
        var range = MediaStreamResult.ParseRange(http.Request.Headers.Range);
        var read = await storage.OpenForServingAsync(tenantId, mediaId, extension, thumbnail ? MediaVariant.Thumbnail : MediaVariant.Original, range, ct);
        return read is null ? Results.NotFound() : new MediaStreamResult(read, contentType, partial: range is not null);
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
    private static async Task<IResult> ChangeAccounts(ChangeAccountsRequest req, CurrentOrg org, FlarelyticsDbContext db, CancellationToken ct)
    {
        var add = req.AddAccountIds ?? [];
        var remove = req.RemoveAccountIds ?? [];
        var accounts = await SocialAccountEndpoints.VisibleAccounts(db, org).ToListAsync(ct);
        if (add.Concat(remove).Any(id => accounts.All(a => a.Id != id))) throw ApiProblem.NotFound("Account");

        var now = DateTime.UtcNow;
        var query = Visible(db, org).Include(p => p.Targets).Include(p => p.Media).Where(p => !p.IsImported && !p.IsInbox);
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
            if (post.ProjectId is { } projectId && accounts.Where(a => add.Contains(a.Id)).FirstOrDefault(a => !a.IsInProject(projectId)) is { } outside)
            {
                results.Add(new ChangeAccountsResult(post.Id, post.Text, post.ScheduledAtUtc, false, $"{outside.Handle ?? outside.Name} non è collegato al progetto del post."));
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
    private static async Task ApplyAsync(SocialPost post, SavePostRequest req, CurrentOrg org, FlarelyticsDbContext db, SocialMediaStore storage, CancellationToken ct)
    {
        // Chi vede solo alcuni progetti scrive solo nei suoi: un post senza progetto è dell'organizzazione.
        if (req.ProjectId is null && !org.CanSee(null))
            throw ApiProblem.BadRequest("project_required", "Scegli il progetto del post.");
        org.EnsureCanSee(req.ProjectId);
        if (req.ProjectId is { } projectId && !await db.Set<Project>().AnyAsync(p => p.Id == projectId, ct))
            throw ApiProblem.NotFound("Progetto");

        post.Update(req.Text, req.ScheduledAtUtc, req.IsDraft || req.Inbox, req.ProjectId);
        if (req.Options is { } options) post.SetOptions(options);
        // In coda (scritto a mano o rimesso lì): una bozza con la data proposta.
        // Un post della coda programmato dal pannello diventa un post qualsiasi;
        // salvato come bozza resta in coda.
        if (req.Inbox) post.PutInInbox(req.SuggestedAtUtc, DateTime.UtcNow);
        else if (!req.IsDraft) post.LeaveInbox();

        // Gli account: si tolgono quelli non più scelti, si aggiungono i nuovi.
        var accounts = await SocialAccountEndpoints.VisibleAccounts(db, org).Where(a => req.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count != req.AccountIds.Distinct().Count()) throw ApiProblem.NotFound("Account");
        EnsureInProject(req.ProjectId, accounts);

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
        var media = await db.Set<SocialMedia>()
            .Where(m => mediaIds.Contains(m.Id) && m.RecurringPostId == null && (m.PostId == null || m.PostId == post.Id)).ToListAsync(ct);
        if (media.Count != mediaIds.Distinct().Count()) throw ApiProblem.NotFound("Immagine");

        foreach (var m in post.Media.Where(m => !mediaIds.Contains(m.Id)).ToList())
        {
            post.RemoveMedia(m);
            db.Remove(m);
            await storage.DeleteAsync(m, CancellationToken.None);
        }
        foreach (var (item, position) in req.Media.Select((m, i) => (m, i)))
        {
            var m = media.Single(x => x.Id == item.Id);
            m.AttachTo(post.Id, position, item.AltText);
            if (!post.Media.Contains(m)) post.AddMedia(m);
        }

        if (post.IsDraft) return;

        // Programmato, un post sta in un progetto (in bozza e in coda può aspettare).
        if (post.ProjectId is null && await HasProjectsAsync(db, ct))
            throw ApiProblem.BadRequest("project_required", "Scegli il progetto del post: un post programmato sta in un progetto.");
        if (post.Targets.Count == 0) throw ApiProblem.BadRequest("no_accounts", "Scegli almeno un account su cui pubblicare.");
        var problems = ProblemsFor(post, accounts, a => post.Targets.Single(t => t.AccountId == a.Id).TextOverride);
        if (problems.Count > 0) throw ApiProblem.BadRequest("post_invalid", string.Join(" ", problems));
    }

    /// <summary>
    /// L'organizzazione ha progetti: allora un post programmato (o ricorrente)
    /// deve stare in uno. Senza progetti (un'installazione appena fatta, solo
    /// social) si programma lo stesso.
    /// </summary>
    public static Task<bool> HasProjectsAsync(FlarelyticsDbContext db, CancellationToken ct) => db.Set<Project>().AnyAsync(ct);

    /// <summary>Anche in bozza: un post di un progetto usa solo gli account collegati a quel progetto.</summary>
    public static void EnsureInProject(Guid? projectId, IEnumerable<SocialAccount> accounts)
    {
        if (projectId is not { } p) return;
        var outside = accounts.Where(a => !a.IsInProject(p)).Select(a => a.Handle ?? a.Name).ToList();
        if (outside.Count > 0)
            throw ApiProblem.BadRequest("account_not_in_project",
                $"{string.Join(", ", outside)}: non {(outside.Count == 1 ? "è collegato" : "sono collegati")} a questo progetto. Collegali da Account social.");
    }

    /// <summary>Quello che impedisce di pubblicare il post su ciascun account, in frasi da mostrare.</summary>
    private static List<string> ProblemsFor(SocialPost post, IEnumerable<SocialAccount> accounts, Func<SocialAccount, string?> textOverride,
        PostOptions? options = null) =>
        ProblemsFor(post.Text, post.Media.OrderBy(m => m.Position).ToList(), post.ProjectId, accounts, textOverride, options ?? post.Options);

    /// <summary>
    /// Lo stesso controllo per un contenuto qualsiasi (un post ricorrente non è
    /// un <see cref="SocialPost"/>). In un progetto si usano solo gli account
    /// collegati a quel progetto.
    /// </summary>
    public static List<string> ProblemsFor(string text, IReadOnlyList<SocialMedia> ordered, Guid? projectId, IEnumerable<SocialAccount> accounts,
        Func<SocialAccount, string?> textOverride, PostOptions options)
    {
        return accounts
            .Select(a => (Account: a, Problems: SocialRules.Problems(textOverride(a) ?? text, ordered, SocialRules.For(a))
                .Concat(SocialRules.OptionProblems(a.Network, options))
                .Concat(projectId is { } p && !a.IsInProject(p) ? ["l'account non è collegato al progetto del post"] : []).ToList()))
            .Where(x => x.Problems.Count > 0)
            .Select(x => $"{NetworkName(x.Account.Network)} ({x.Account.Handle ?? x.Account.Name}): {string.Join("; ", x.Problems)}.")
            .ToList();
    }

    public static string NetworkName(SocialNetwork n) => n switch
    {
        SocialNetwork.FacebookPage => "Facebook",
        _ => n.ToString()
    };

    private static async Task<SocialPost> LoadAsync(FlarelyticsDbContext db, CurrentOrg org, Guid postId, CancellationToken ct) =>
        await Visible(db, org).Include(p => p.Targets).Include(p => p.Media).SingleOrDefaultAsync(p => p.Id == postId, ct)
        ?? throw ApiProblem.NotFound("Post");
}

public enum SocialPostStatus { Draft, Scheduled, Publishing, Published, PartiallyFailed, Failed, Inbox }

/// <param name="Imported">Pubblicato fuori da ElephantSight e copiato qui: si legge e basta.</param>
/// <param name="Inbox">Arrivato con una chiave API e in attesa nella coda "Da programmare".</param>
/// <param name="SuggestedAtUtc">La data proposta da chi l'ha mandato.</param>
/// <param name="Source">Il nome della chiave API da cui è arrivato (solo nell'elenco della coda).</param>
/// <param name="RecurringPostId">Un'uscita di questo post ricorrente.</param>
public record SocialPostResponse(Guid Id, string Text, DateTime ScheduledAtUtc, bool IsDraft, Guid? ProjectId, SocialPostStatus Status, bool Editable, bool Imported,
    bool Inbox, DateTime? SuggestedAtUtc, string? ExternalRef, PostOptions Options, IReadOnlyList<SocialMediaResponse> Media, IReadOnlyList<SocialTargetResponse> Targets, DateTime CreatedAtUtc,
    Guid? RecurringPostId)
{
    public string? Source { get; init; }

    public static SocialPostResponse From(SocialPost p, MediaUrlSigner signer) => new(
        p.Id, p.Text, p.ScheduledAtUtc, p.IsDraft, p.ProjectId, StatusOf(p), p.IsEditable, p.IsImported,
        p.IsInbox, p.SuggestedAtUtc, p.ExternalRef, p.Options,
        p.Media.OrderBy(m => m.Position).Select(m => SocialMediaResponse.From(m, signer)).ToList(),
        p.Targets.OrderBy(t => t.Network).ThenBy(t => t.AccountName).Select(SocialTargetResponse.From).ToList(),
        p.CreatedAtUtc, p.RecurringPostId);

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

/// <param name="Url">L'originale, firmato: vale per le anteprime del pannello finché la pagina resta aperta un pomeriggio. Null se è stato cancellato dopo la pubblicazione.</param>
/// <param name="ThumbnailUrl">La miniatura (~400 px), se c'è: resta anche senza l'originale.</param>
/// <param name="OriginalDeleted">L'originale è stato cancellato dopo la pubblicazione: resta la miniatura (se c'è) e il link del post sulla rete.</param>
public record SocialMediaResponse(Guid Id, MediaKind Kind, int Width, int Height, long SizeBytes, int? DurationMs, bool FastStart, string? AltText, string? Url,
    string? ThumbnailUrl, bool OriginalDeleted)
{
    public static readonly TimeSpan PreviewValidity = TimeSpan.FromHours(12);

    public static SocialMediaResponse From(SocialMedia m, MediaUrlSigner signer)
    {
        var expires = DateTime.UtcNow.Add(PreviewValidity);
        return new(m.Id, m.Kind, m.Width, m.Height, m.SizeBytes, m.DurationMs, m.FastStart, m.AltText,
            m.HasOriginal ? signer.PathFor(m, expires) : null,
            m.HasThumbnail ? signer.ThumbnailPathFor(m, expires) : null,
            !m.HasOriginal);
    }
}

public record SavePostMedia(Guid Id, string? AltText);

public record CopyMediaRequest(IReadOnlyList<Guid> Ids);

public class CopyMediaRequestValidator : AbstractValidator<CopyMediaRequest>
{
    public CopyMediaRequestValidator() => RuleFor(x => x.Ids).NotEmpty().Must(i => i.Count <= 10);
}

/// <param name="ScheduledAtUtc">Un'ora per tutti; null = ciascuno all'ora che ha proposto.</param>
/// <param name="Options">Le scelte per le reti che le chiedono (TikTok), uguali per tutti i post assegnati.</param>
/// <param name="ProjectId">Il progetto dei post che non ne hanno uno; quelli che ce l'hanno restano nel loro.</param>
public record AssignInboxRequest(IReadOnlyList<Guid> PostIds, IReadOnlyList<Guid> AccountIds, DateTime? ScheduledAtUtc, PostOptions? Options = null, Guid? ProjectId = null);

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
/// <param name="Inbox">Nella coda "Da programmare" invece che nel calendario: una bozza, con <paramref name="SuggestedAtUtc"/> come data proposta (facoltativa).</param>
public record SavePostRequest(string Text, DateTime ScheduledAtUtc, bool IsDraft, Guid? ProjectId, IReadOnlyList<Guid> AccountIds,
    IReadOnlyList<SavePostMedia> Media, Dictionary<Guid, string>? Overrides, PostOptions? Options = null, bool Inbox = false, DateTime? SuggestedAtUtc = null);

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
