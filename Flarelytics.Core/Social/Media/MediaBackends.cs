using System.Net;
using System.Net.Http.Headers;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Social.Media;

/// <summary>
/// Lo storage dei file dei post non è utilizzabile: modalità remota imposta
/// senza Bunny configurato, o Bunny che rifiuta o non risponde. Il messaggio
/// si può mostrare: non contiene mai la chiave d'accesso.
/// </summary>
public class MediaStorageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Un intervallo di byte chiesto (come l'header <c>Range</c>): da-a, da in poi, o gli ultimi N (<see cref="Suffix"/>).</summary>
public readonly record struct ByteRange(long? From, long? To)
{
    public bool Suffix => From is null;

    /// <summary>L'intervallo concreto su un file di <paramref name="total"/> byte; null se non si può soddisfare (416).</summary>
    public (long From, long To)? Resolve(long total)
    {
        if (total == 0) return null;
        if (From is null)
        {
            if (To is not > 0) return null;
            return (Math.Max(0, total - To.Value), total - 1);
        }
        if (From >= total) return null;
        return (From.Value, Math.Min(To ?? total - 1, total - 1));
    }
}

/// <summary>
/// Un file aperto: <see cref="Content"/> restituisce esattamente i byte da
/// <see cref="From"/> a <see cref="To"/> (compresi) di un file lungo
/// <see cref="TotalLength"/>. <see cref="Satisfiable"/> è falso se
/// l'intervallo chiesto non esiste (risposta 416).
/// </summary>
public sealed class MediaRead(Stream content, long totalLength, long from, long to) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;
    public long TotalLength { get; } = totalLength;
    public long From { get; } = from;
    public long To { get; } = to;
    public bool Satisfiable => To >= From || TotalLength == 0;
    public long Length => Math.Max(0, To - From + 1);

    public static MediaRead Unsatisfiable(long total) => new(Stream.Null, total, 1, 0);

    public ValueTask DisposeAsync() => Content.DisposeAsync();
    public void Dispose() => Content.Dispose();
}

/// <summary>Dove sta un file: il disco del server o Bunny. Le operazioni sono le stesse.</summary>
public interface IMediaBackend
{
    MediaLocation Location { get; }

    /// <summary>Scrive <paramref name="length"/> byte da <paramref name="content"/>, sostituendo quello che c'era.</summary>
    Task PutAsync(MediaKey key, string extension, Stream content, long length, CancellationToken ct);

    /// <summary>Apre il file (o un suo intervallo); null se non c'è.</summary>
    Task<MediaRead?> OpenAsync(MediaKey key, string extension, ByteRange? range, CancellationToken ct);

    /// <summary>Cancella il file; non c'è più già da prima va bene lo stesso.</summary>
    Task DeleteAsync(MediaKey key, string extension, CancellationToken ct);
}

/// <summary>
/// Il disco del server, come prima: <c>{Reports}/_social/{tenant}/{id}.jpg|.mp4|.mov</c>
/// e la miniatura <c>{id}.thumb.jpg</c>. In chiaro: il disco è del server, e
/// gli originali si possono servire con il supporto Range di ASP.NET.
/// </summary>
public class LocalMediaBackend(IOptions<ReportsOptions> options) : IMediaBackend
{
    public MediaLocation Location => MediaLocation.Local;

    public string Root => Path.Combine(options.Value.StorageDirectory, "_social");

    public string PathFor(MediaKey key, string extension) =>
        Path.Combine(Root, key.TenantId.ToString("N"), key.MediaId.ToString("N") + (key.Variant == MediaVariant.Thumbnail ? ".thumb.jpg" : extension));

    public async Task PutAsync(MediaKey key, string extension, Stream content, long length, CancellationToken ct)
    {
        var path = PathFor(key, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    /// <summary>Un file già su disco (un video appena caricato) prende il suo posto senza essere copiato.</summary>
    public void MoveIn(MediaKey key, string extension, string sourcePath)
    {
        var path = PathFor(key, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Move(sourcePath, path, overwrite: true);
    }

    public Task<MediaRead?> OpenAsync(MediaKey key, string extension, ByteRange? range, CancellationToken ct)
    {
        var path = PathFor(key, extension);
        if (!File.Exists(path)) return Task.FromResult<MediaRead?>(null);

        var file = File.OpenRead(path);
        var total = file.Length;
        var (from, to) = range is { } r ? r.Resolve(total) ?? (-1, -1) : (0, total - 1);
        if (from < 0)
        {
            file.Dispose();
            return Task.FromResult<MediaRead?>(MediaRead.Unsatisfiable(total));
        }
        file.Seek(from, SeekOrigin.Begin);
        return Task.FromResult<MediaRead?>(new MediaRead(new LimitedStream(file, to - from + 1), total, from, to));
    }

    public Task DeleteAsync(MediaKey key, string extension, CancellationToken ct)
    {
        var path = PathFor(key, extension);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bunny Storage, cifrato (<see cref="MediaCipher"/>): Bunny vede solo
/// <c>elephantsight/social/{tenant}/{id}[.thumb].bin</c>, senza tipo né contenuto leggibile.
/// </summary>
/// <remarks>
/// Lo Storage API non documenta le richieste <c>Range</c>: si chiedono lo
/// stesso, e se Bunny risponde con il file intero (200 invece di 206) si
/// legge dall'inizio in streaming scartando quello che precede. Funziona in
/// tutti e due i casi; nel secondo si spreca solo banda.
/// </remarks>
public class BunnyMediaBackend(BunnyStorageClient bunny, MediaCipher cipher) : IMediaBackend
{
    public MediaLocation Location => MediaLocation.Remote;

    public static string PathFor(MediaKey key) =>
        $"social/{key.TenantId:N}/{key.MediaId:N}{(key.Variant == MediaVariant.Thumbnail ? ".thumb" : "")}.bin";

    public async Task PutAsync(MediaKey key, string extension, Stream content, long length, CancellationToken ct)
    {
        await using var encrypted = cipher.Encrypt(content, length, key);
        await bunny.PutAsync(PathFor(key), encrypted, MediaCipher.EncryptedLength(length), ct);
    }

    public async Task<MediaRead?> OpenAsync(MediaKey key, string extension, ByteRange? range, CancellationToken ct)
    {
        var path = PathFor(key);
        // Senza intervallo: una richiesta sola, intestazione e blocchi dallo stesso flusso.
        var first = await bunny.GetAsync(path, 0, range is null ? null : MediaCipher.HeaderSize - 1, ct);
        if (first is null) return null;

        var stream = await first.Content.ReadAsStreamAsync(ct);
        var whole = first.StatusCode != HttpStatusCode.PartialContent;
        MediaHeader header;
        try
        {
            header = cipher.ReadHeader(await ReadExactlyAsync(stream, MediaCipher.HeaderSize, ct), key);
        }
        catch
        {
            first.Dispose();
            throw;
        }

        var (from, to) = range is { } r ? r.Resolve(header.Length) ?? (-1, -1) : (0, header.Length - 1);
        if (from < 0 || header.Length == 0)
        {
            var total = header.Length;
            header.Dispose();
            first.Dispose();
            return from < 0 ? MediaRead.Unsatisfiable(total) : new MediaRead(Stream.Null, 0, 0, -1);
        }

        var firstBlock = from / header.BlockSize;
        var lastBlock = to / header.BlockSize;
        var start = header.EncryptedOffset(firstBlock);
        var end = header.EncryptedOffset(lastBlock) + header.PlainLength(lastBlock) + MediaCipher.TagSize - 1;

        HttpResponseMessage? owner = first;
        try
        {
            if (whole)
            {
                // Il flusso è il file intero, già dopo l'intestazione: si salta fino al primo blocco che serve.
                await SkipAsync(stream, start - MediaCipher.HeaderSize, ct);
            }
            else
            {
                first.Dispose();
                owner = null;
                owner = await bunny.GetAsync(path, start, end, ct) ?? throw new FileNotFoundException("Il file è sparito da Bunny durante la lettura.");
                stream = await owner.Content.ReadAsStreamAsync(ct);
                if (owner.StatusCode != HttpStatusCode.PartialContent) await SkipAsync(stream, start, ct);
            }
        }
        catch
        {
            // La DEK in chiaro non resta in memoria nemmeno quando la lettura fallisce.
            header.Dispose();
            owner?.Dispose();
            throw;
        }

        var plain = MediaCipher.Decrypt(new OwnedStream(stream, owner), header, key, from, to);
        return new MediaRead(plain, header.Length, from, to);
    }

    public Task DeleteAsync(MediaKey key, string extension, CancellationToken ct) => bunny.DeleteAsync(PathFor(key), ct);

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = await stream.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, ct);
        if (read != count) throw new System.Security.Cryptography.CryptographicException("Il media cifrato è incompleto.");
        return buffer;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0) throw new System.Security.Cryptography.CryptographicException("Il media cifrato è incompleto.");
            count -= read;
        }
    }
}

/// <summary>
/// Lo Storage API di Bunny via HTTP, senza SDK: <c>PUT</c>, <c>GET</c>,
/// <c>DELETE</c> su <c>https://{host della regione}/{zone}/{percorso}</c> con
/// la password della zone nell'header <c>AccessKey</c>.
/// </summary>
/// <remarks>
/// La password non compare mai negli errori: Bunny risponde 401 sia per una
/// password sbagliata sia per l'host di un'altra regione, e il messaggio lo dice.
/// </remarks>
public class BunnyStorageClient(HttpClient http, IOptionsMonitor<MediaStorageOptions> options)
{
    /// <summary>
    /// Tutto quello che scriviamo sta in questa cartella, mai nella radice:
    /// la zone può servire anche ad altro, e i nostri file si riconoscono.
    /// </summary>
    public const string RootFolder = "elephantsight";

    private BunnyStorageOptions Bunny => options.CurrentValue.Bunny;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var b = Bunny;
        if (!b.Configured) throw new MediaStorageUnavailableException("Bunny Storage non è configurato.");
        var request = new HttpRequestMessage(method, $"https://{b.Host}/{Uri.EscapeDataString(b.StorageZone!.Trim())}/{RootFolder}/{path}");
        request.Headers.Add("AccessKey", b.AccessKey!.Trim());
        return request;
    }

    public async Task PutAsync(string path, Stream content, long length, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, path);
        request.Content = new StreamContent(content, 81920);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK)) throw Failure(response.StatusCode, "il caricamento");
    }

    /// <summary>Il file, o un suo intervallo (<paramref name="from"/>-<paramref name="to"/>); null se non c'è. Chi lo riceve chiude la risposta.</summary>
    public async Task<HttpResponseMessage?> GetAsync(string path, long? from, long? to, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        if (from is not null && (from > 0 || to is not null)) request.Headers.Range = new RangeHeaderValue(from, to);
        var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw Failure(status, "la lettura");
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, path);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound)) throw Failure(response.StatusCode, "la cancellazione");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, completion, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MediaStorageUnavailableException($"Bunny Storage non risponde ({Bunny.Host}): {e.Message}", e);
        }
    }

    private MediaStorageUnavailableException Failure(HttpStatusCode status, string what) => status == HttpStatusCode.Unauthorized
        ? new MediaStorageUnavailableException($"Bunny Storage rifiuta l'accesso per {what}: controlla la password della storage zone e la regione ({Bunny.Host}).")
        : new MediaStorageUnavailableException($"Bunny Storage ha risposto {(int)status} per {what}.");
}

/// <summary>Legge al massimo <paramref name="limit"/> byte dal flusso sottostante: un intervallo di un file su disco.</summary>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _remaining = limit;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => limit;
    public override long Position { get => limit - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var n = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0) return 0;
        var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
        _remaining -= n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Un flusso che, chiuso, chiude anche la risposta HTTP da cui viene.</summary>
internal sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
