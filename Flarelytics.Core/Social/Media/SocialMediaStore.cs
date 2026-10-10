using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social.Media;

/// <summary>Che storage hanno i file nuovi dei post.</summary>
public enum MediaStorageStatus
{
    Local,
    Remote,

    /// <summary>Modalità remota imposta e Bunny non configurato: niente caricamenti.</summary>
    Unavailable
}

/// <summary>
/// I file dei post (immagini, video, miniature): dove si scrivono, dove si
/// leggono, le copie, i temporanei. È l'unico punto che sa che esistono due
/// storage; il resto del codice lavora con i <see cref="SocialMedia"/>.
/// </summary>
/// <remarks>
/// <para>I file nuovi vanno nello storage attivo (<see cref="Status"/>); quelli
/// già scritti si leggono e si cancellano dove stanno
/// (<see cref="SocialMedia.Location"/>), così cambiare configurazione non
/// rompe niente e non serve migrare.</para>
///
/// <para>I temporanei (un video caricato a pezzi, letto da <see cref="Mp4Info"/>
/// prima di spedirlo) stanno in <c>{Reports}/_social-tmp</c> e si cancellano
/// appena usati; il worker toglie quelli abbandonati. In modalità remota sono
/// gli unici file dei post che toccano il disco.</para>
/// </remarks>
public class SocialMediaStore(
    LocalMediaBackend local, BunnyMediaBackend remote, IOptionsMonitor<MediaStorageOptions> options, IOptions<ReportsOptions> reports,
    ILogger<SocialMediaStore> log)
{
    public const string UnavailableMessage =
        "Il caricamento di immagini e video non è disponibile: questa installazione usa solo lo storage remoto (MEDIA_STORAGE=remote) " +
        "e Bunny Storage non è configurato. Un amministratore dell'istanza lo configura in Impostazioni dell'istanza → Storage dei file.";

    public const string UnavailableLockedMessage =
        "Il caricamento di immagini e video non è disponibile: lo storage dei file non è configurato. Lo configura chi gestisce l'installazione.";

    public MediaStorageStatus Status
    {
        get
        {
            var o = options.CurrentValue;
            return o.Storage switch
            {
                MediaStorageMode.Local => MediaStorageStatus.Local,
                MediaStorageMode.Remote => o.Bunny.Configured ? MediaStorageStatus.Remote : MediaStorageStatus.Unavailable,
                _ => o.Bunny.Configured ? MediaStorageStatus.Remote : MediaStorageStatus.Local
            };
        }
    }

    /// <summary>La modalità remota è imposta dall'ambiente: il disco non si usa per i file dei post.</summary>
    public bool RemoteForced => options.CurrentValue.Storage == MediaStorageMode.Remote;

    /// <summary>Dove vanno i file nuovi; se non c'è uno storage utilizzabile, l'errore da mostrare.</summary>
    public MediaLocation WritableLocation => Status switch
    {
        MediaStorageStatus.Local => MediaLocation.Local,
        MediaStorageStatus.Remote => MediaLocation.Remote,
        _ => throw new MediaStorageUnavailableException(options.CurrentValue.Locked ? UnavailableLockedMessage : UnavailableMessage)
    };

    /// <summary>Bunny si configura solo dall'ambiente: il pannello non ne mostra la sezione.</summary>
    public bool Locked => options.CurrentValue.Locked;

    private IMediaBackend For(MediaLocation location) => location == MediaLocation.Remote ? remote : local;

    private static MediaKey Original(SocialMedia m) => new(m.TenantId, m.Id, MediaVariant.Original);
    private static MediaKey Thumbnail(SocialMedia m) => new(m.TenantId, m.Id, MediaVariant.Thumbnail);

    // --- scrittura ---

    /// <summary>Scrive l'originale nello storage attivo (immagini: piccole, già in memoria).</summary>
    public async Task SaveAsync(SocialMedia media, byte[] content, CancellationToken ct)
    {
        var location = WritableLocation;
        media.SetLocation(location);
        await For(location).PutAsync(Original(media), media.Extension, new MemoryStream(content, writable: false), content.Length, ct);
    }

    /// <summary>
    /// Un file temporaneo (un video) diventa l'originale: sul disco si sposta,
    /// su Bunny si cifra e si carica in streaming. Il temporaneo si cancella
    /// comunque, anche se qualcosa va storto.
    /// </summary>
    public async Task SaveFromTempAsync(SocialMedia media, string tempPath, CancellationToken ct)
    {
        try
        {
            var location = WritableLocation;
            media.SetLocation(location);
            if (location == MediaLocation.Local)
            {
                local.MoveIn(Original(media), media.Extension, tempPath);
                return;
            }
            await using var file = File.OpenRead(tempPath);
            await remote.PutAsync(Original(media), media.Extension, file, file.Length, ct);
        }
        finally
        {
            DeleteQuietly(tempPath);
        }
    }

    /// <summary>La miniatura, accanto all'originale (stesso storage).</summary>
    public async Task SaveThumbnailAsync(SocialMedia media, byte[] jpeg, CancellationToken ct)
    {
        // Sul disco no, se la modalità remota è imposta: la miniatura segue l'originale solo dove si può scrivere.
        if (media.Location == MediaLocation.Local && RemoteForced) throw new MediaStorageUnavailableException(Locked ? UnavailableLockedMessage : UnavailableMessage);
        await For(media.Location).PutAsync(Thumbnail(media), ".jpg", new MemoryStream(jpeg, writable: false), jpeg.Length, ct);
        media.MarkThumbnail();
    }

    // --- lettura ---

    /// <summary>L'originale intero in memoria (le immagini per Bluesky e Mastodon).</summary>
    /// <exception cref="FileNotFoundException">L'originale non c'è più (cancellato dopo la pubblicazione, o perso).</exception>
    public async Task<byte[]> ReadAllAsync(SocialMedia media, CancellationToken ct)
    {
        await using var read = await OpenOriginalAsync(media, null, ct);
        var buffer = new byte[read.Length];
        await read.Content.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    /// <summary>L'originale come flusso, dall'inizio (un video per TikTok, a pezzi). La lunghezza è <see cref="SocialMedia.SizeBytes"/>.</summary>
    public async Task<Stream> OpenReadAsync(SocialMedia media, CancellationToken ct) => (await OpenOriginalAsync(media, null, ct)).Content;

    private async Task<MediaRead> OpenOriginalAsync(SocialMedia media, ByteRange? range, CancellationToken ct)
    {
        if (!media.HasOriginal) throw new FileNotFoundException("L'originale è stato cancellato dopo la pubblicazione.");
        return await For(media.Location).OpenAsync(Original(media), media.Extension, range, ct)
            ?? throw new FileNotFoundException("Il file del media non c'è più.");
    }

    /// <summary>
    /// Per la rotta pubblica, che conosce solo tenant e media (dalla firma):
    /// prima il disco, poi Bunny se è configurato. Null se il file non c'è.
    /// </summary>
    public async Task<MediaRead?> OpenForServingAsync(Guid tenantId, Guid mediaId, string extension, MediaVariant variant, ByteRange? range, CancellationToken ct)
    {
        var key = new MediaKey(tenantId, mediaId, variant);
        if (await local.OpenAsync(key, extension, range, ct) is { } found) return found;
        return options.CurrentValue.Bunny.Configured ? await remote.OpenAsync(key, extension, range, ct) : null;
    }

    // --- copie ---

    /// <summary>
    /// Copia originale e miniatura su un altro media (uscite dei post
    /// ricorrenti, "Duplica"), nello storage attivo. Da Bunny a Bunny passa
    /// dal server: si scarica, si decifra, si ricifra con la DEK e i dati
    /// associati del media nuovo, si carica. Niente in chiaro su disco.
    /// </summary>
    public async Task CopyAsync(SocialMedia source, SocialMedia copy, CancellationToken ct)
    {
        var location = WritableLocation;
        copy.SetLocation(location);
        await using (var read = await OpenOriginalAsync(source, null, ct))
        {
            await For(location).PutAsync(Original(copy), copy.Extension, read.Content, read.Length, ct);
        }
        if (source.HasThumbnail && await For(source.Location).OpenAsync(Thumbnail(source), ".jpg", null, ct) is { } thumb)
        {
            await using (thumb)
            {
                await For(location).PutAsync(Thumbnail(copy), ".jpg", thumb.Content, thumb.Length, ct);
            }
            copy.MarkThumbnail();
        }
    }

    // --- cancellazione ---

    /// <summary>
    /// Toglie originale e miniatura. Si chiama dopo aver tolto il media dal
    /// database: se lo storage remoto non risponde, il file resta orfano lì e
    /// lo si scrive nel log, invece di far fallire un'operazione già fatta.
    /// </summary>
    public async Task DeleteAsync(SocialMedia media, CancellationToken ct = default)
    {
        try
        {
            if (media.HasOriginal) await For(media.Location).DeleteAsync(Original(media), media.Extension, ct);
            if (media.HasThumbnail) await For(media.Location).DeleteAsync(Thumbnail(media), ".jpg", ct);
        }
        catch (MediaStorageUnavailableException e)
        {
            log.LogWarning("File del media {Media} non cancellato dallo storage remoto: {Message}", media.Id, e.Message);
        }
    }

    /// <summary>Solo l'originale (la pulizia dopo la pubblicazione): un errore qui fa riprovare al giro dopo.</summary>
    public async Task DeleteOriginalAsync(SocialMedia media, CancellationToken ct)
    {
        if (media.HasOriginal) await For(media.Location).DeleteAsync(Original(media), media.Extension, ct);
    }

    // --- temporanei ---

    public string TempRoot => Path.Combine(reports.Value.StorageDirectory, "_social-tmp");

    /// <summary>Il temporaneo di un caricamento (il video a pezzi): sempre sul disco, anche in modalità remota.</summary>
    public string TempPath(Guid tenantId, Guid uploadId)
    {
        var path = Path.Combine(TempRoot, tenantId.ToString("N"), uploadId.ToString("N") + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public void DeleteTemp(Guid tenantId, Guid uploadId) => DeleteQuietly(Path.Combine(TempRoot, tenantId.ToString("N"), uploadId.ToString("N") + ".part"));

    /// <summary>
    /// I temporanei abbandonati (caricamenti mai completati, o rimasti dopo un
    /// riavvio), e i <c>.part</c> che le versioni di prima lasciavano accanto ai media.
    /// </summary>
    public int DeleteStaleTemp(TimeSpan olderThan)
    {
        var limit = DateTime.UtcNow - olderThan;
        var stale = (Directory.Exists(TempRoot) ? Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories) : [])
            .Concat(Directory.Exists(local.Root) ? Directory.EnumerateFiles(local.Root, "*.part", SearchOption.AllDirectories) : [])
            .Where(f => File.GetLastWriteTimeUtc(f) < limit).ToList();
        foreach (var f in stale) DeleteQuietly(f);
        return stale.Count;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Lo toglie il worker al giro di pulizia.
        }
    }
}
