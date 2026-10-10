using Flarelytics.Core.Social.Media;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Flarelytics.Api.Features.Social;

/// <summary>
/// Un file dei post in risposta, dal disco o decifrato da Bunny, con il
/// supporto a <c>Range</c> (206 / 416). Non usa <c>Results.File</c> perché da
/// Bunny non c'è un file né un flusso con seek: l'intervallo lo risolve lo
/// storage, qui si scrivono solo intestazioni e byte.
/// </summary>
public sealed class MediaStreamResult(MediaRead read, string contentType, bool partial) : IResult
{
    /// <summary>
    /// Un solo intervallo, <c>bytes=a-b</c>, <c>bytes=a-</c> o <c>bytes=-n</c>.
    /// Più intervalli insieme o una sintassi strana: si ignora e si manda tutto,
    /// come concede l'RFC 9110.
    /// </summary>
    public static ByteRange? ParseRange(StringValues header)
    {
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = value["bytes=".Length..].Trim();
        if (spec.Contains(',')) return null;
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var (first, last) = (spec[..dash].Trim(), spec[(dash + 1)..].Trim());
        long? from = first.Length == 0 ? null : long.TryParse(first, out var f) && f >= 0 ? f : -1;
        long? to = last.Length == 0 ? null : long.TryParse(last, out var t) && t >= 0 ? t : -1;
        if (from == -1 || to == -1 || (from is null && to is null) || (from is { } a && to is { } b && b < a)) return null;
        return new ByteRange(from, to);
    }

    public async Task ExecuteAsync(HttpContext http)
    {
        await using (read)
        {
            var response = http.Response;
            response.Headers.AcceptRanges = "bytes";
            // L'indirizzo è firmato e scade: il browser può tenerlo, una cache condivisa no.
            response.Headers.CacheControl = "private, max-age=3600";

            if (!read.Satisfiable)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = new ContentRangeHeaderValue(read.TotalLength).ToString();
                return;
            }

            response.ContentType = contentType;
            response.ContentLength = read.Length;
            if (partial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = new ContentRangeHeaderValue(read.From, read.To, read.TotalLength).ToString();
            }

            try
            {
                await read.Content.CopyToAsync(response.Body, 81920, http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // Il player ha chiuso la connessione (succede di continuo cercando nel video).
            }
        }
    }
}
