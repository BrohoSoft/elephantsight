using Flarelytics.Api.Common;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Core.Social.Media;
using FluentValidation;
using Microsoft.AspNetCore.Http.Features;

namespace Flarelytics.Api.Features.Social;

/// <summary>
/// Immagini e video dei post in arrivo: da un campo di un multipart, o un
/// video grande a pezzi. Lo usano il pannello e l'API pubblica.
/// </summary>
/// <remarks>
/// <para>Le immagini devono essere già JPEG (le converte il pannello, o chi
/// usa l'API). I video arrivano come sono, MP4 o MOV: se ne leggono durata,
/// dimensioni e posizione dell'indice, senza ricodificarli.</para>
///
/// <para><b>A pezzi.</b> Davanti all'istanza c'è spesso un proxy con un tetto
/// alla singola richiesta (Cloudflare gratuito: 100 MB), e un video da telefono
/// lo supera facilmente. Si apre un caricamento, si mandano pezzi fino a
/// <see cref="MaxChunkBytes"/>, e alla fine il file diventa un video del post.</para>
/// </remarks>
public static class SocialMediaFiles
{
    public const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Il più grande che serve: TikTok accetta fino a 4 GB, Instagram 300 MB.</summary>
    public const long MaxVideoBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Un pezzo: sotto i 100 MB di Cloudflare, con margine.</summary>
    public const long MaxChunkBytes = 50L * 1024 * 1024;

    /// <summary>Una richiesta multipart con un video intero: oltre, si carica a pezzi.</summary>
    public const long MaxSingleRequestBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Il file di un campo multipart diventa un'immagine o un video, già nello
    /// storage attivo (disco o Bunny). Il chiamante lo aggiunge al contesto; se
    /// poi qualcosa va storto, cancella il file con <see cref="SocialMediaStore.DeleteAsync"/>.
    /// </summary>
    public static async Task<SocialMedia> FromFormFileAsync(IFormFile file, Guid tenantId, Guid userId, SocialMediaStore storage, CancellationToken ct)
    {
        _ = storage.WritableLocation; // senza storage utilizzabile ci si ferma prima di leggere il file
        if (file.Length == 0) throw ApiProblem.BadRequest("file_size", $"Il file '{file.Name}' è vuoto.");
        var fileName = Path.GetFileName(file.FileName);

        // I primi byte dicono se è un JPEG.
        var head = new byte[2];
        await using (var peek = file.OpenReadStream()) _ = await peek.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, ct);

        if (head is [0xFF, 0xD8])
        {
            if (file.Length > MaxImageBytes) throw ApiProblem.BadRequest("file_size", $"L'immagine '{fileName}' deve pesare meno di 10 MB.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var content = buffer.ToArray();
            if (!JpegInfo.TryReadSize(content, out var width, out var height))
                throw ApiProblem.BadRequest("file_type", $"Non riesco a leggere il JPEG '{fileName}'.");

            var image = SocialMedia.Create(tenantId, fileName, content.Length, width, height, userId);
            await storage.SaveAsync(image, content, ct);
            return image;
        }

        // Altrimenti dev'essere un video: si scrive in un temporaneo e se ne legge l'indice da lì.
        var partId = Guid.NewGuid();
        var part = storage.TempPath(tenantId, partId);
        try
        {
            await using (var target = File.Create(part))
            {
                await file.CopyToAsync(target, ct);
            }
        }
        catch
        {
            storage.DeleteTemp(tenantId, partId);
            throw;
        }
        return await VideoFromTempAsync(tenantId, userId, partId, fileName, storage, ct);
    }

    /// <summary>
    /// Un video intero nel temporaneo diventa un video del post, nello storage
    /// attivo; se non è un video, si rifiuta. Il temporaneo si cancella in
    /// ogni caso.
    /// </summary>
    public static async Task<SocialMedia> VideoFromTempAsync(Guid tenantId, Guid userId, Guid partId, string fileName, SocialMediaStore storage, CancellationToken ct)
    {
        var part = storage.TempPath(tenantId, partId);
        VideoInfo? info;
        long size;
        try
        {
            using (var stream = File.OpenRead(part))
            {
                size = stream.Length;
                info = Mp4Info.TryRead(stream);
            }
        }
        catch
        {
            storage.DeleteTemp(tenantId, partId);
            throw;
        }

        if (info is null || size > MaxVideoBytes)
        {
            storage.DeleteTemp(tenantId, partId);
            throw info is null
                ? ApiProblem.BadRequest("file_type", $"'{fileName}' non è né un'immagine JPEG né un video MP4/MOV leggibile.")
                : ApiProblem.BadRequest("file_size", "Il video supera i 4 GB.");
        }

        var video = SocialMedia.CreateVideo(tenantId, fileName, size, info, userId);
        await storage.SaveFromTempAsync(video, part, ct);
        return video;
    }

    /// <summary>
    /// Le rotte del caricamento a pezzi, sotto un gruppo che ha già scelto il
    /// tenant (pannello o chiave API). <paramref name="who"/> dice tenant e
    /// utente della richiesta.
    /// </summary>
    public static void MapChunkedUploads(this RouteGroupBuilder group, Func<HttpContext, (Guid TenantId, Guid UserId)> who)
    {
        group.MapPost("/media/uploads", (StartUploadRequest req, HttpContext http, SocialMediaStore storage) =>
        {
            // Senza uno storage dove mettere il video, meglio dirlo prima di caricare gigabyte.
            _ = storage.WritableLocation;
            if (req.Size is <= 0 or > MaxVideoBytes) throw ApiProblem.BadRequest("file_size", "Un video da 1 byte a 4 GB.");
            var uploadId = Guid.NewGuid();
            File.Create(storage.TempPath(who(http).TenantId, uploadId)).Dispose();
            return Results.Ok(new StartUploadResponse(uploadId, MaxChunkBytes));
        }).Validating<StartUploadRequest>();

        // Il pezzo nel corpo, così com'è. Rimandare un pezzo già arrivato (dopo
        // un errore di rete) non lo aggiunge due volte: conta l'offset.
        group.MapPut("/media/uploads/{uploadId:guid}", async (Guid uploadId, long offset, HttpContext http, SocialMediaStore storage, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxChunkBytes + 1024;
            var path = storage.TempPath(who(http).TenantId, uploadId);
            if (!File.Exists(path)) throw ApiProblem.NotFound("Caricamento");

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
            if (offset > file.Length) throw ApiProblem.BadRequest("offset", $"Manca un pezzo: il server ha {file.Length} byte, il pezzo parte da {offset}.");
            if (offset < 0) throw ApiProblem.BadRequest("offset", "Offset non valido.");

            file.Seek(offset, SeekOrigin.Begin);
            await http.Request.Body.CopyToAsync(file, ct);
            if (file.Length > MaxVideoBytes) throw ApiProblem.BadRequest("file_size", "Il video supera i 4 GB.");
            return Results.Ok(new { received = file.Length });
        });

        group.MapPost("/media/uploads/{uploadId:guid}/complete", async (Guid uploadId, CompleteUploadRequest req, HttpContext http,
            FlarelyticsDbContext db, SocialMediaStore storage, MediaUrlSigner signer, CancellationToken ct) =>
        {
            var (tenantId, userId) = who(http);
            if (!File.Exists(storage.TempPath(tenantId, uploadId))) throw ApiProblem.NotFound("Caricamento");

            var video = await VideoFromTempAsync(tenantId, userId, uploadId, Path.GetFileName(req.FileName), storage, ct);
            db.Add(video);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                await storage.DeleteAsync(video, CancellationToken.None);
                throw;
            }
            return Results.Ok(SocialMediaResponse.From(video, signer));
        }).Validating<CompleteUploadRequest>();
    }
}

public record StartUploadRequest(string FileName, long Size);

/// <param name="ChunkBytes">La dimensione massima di ogni pezzo.</param>
public record StartUploadResponse(Guid UploadId, long ChunkBytes);

public record CompleteUploadRequest(string FileName);

public class StartUploadRequestValidator : AbstractValidator<StartUploadRequest>
{
    public StartUploadRequestValidator() => RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
}

public class CompleteUploadRequestValidator : AbstractValidator<CompleteUploadRequest>
{
    public CompleteUploadRequestValidator() => RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
}
