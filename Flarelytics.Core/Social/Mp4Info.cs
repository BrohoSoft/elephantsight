using System.Buffers.Binary;
using System.Text;

namespace Flarelytics.Core.Social;

/// <param name="FastStart">L'indice (moov) sta prima dei dati (mdat): Instagram lo pretende per i Reel.</param>
public record VideoInfo(int Width, int Height, int DurationMs, bool FastStart, string ContentType);

/// <summary>
/// Le caratteristiche di un video MP4 o MOV, lette dalle "box" del file
/// senza decodificarlo: durata dall'mvhd, dimensioni dal tkhd della traccia
/// video (girate se la matrice dice che il video è ruotato, come quelli dei
/// telefoni in verticale), e la posizione dell'indice.
/// </summary>
/// <remarks>
/// Si legge dal file su disco a salti: un video può pesare centinaia di MB, e
/// serve solo l'indice, che di solito è di pochi KB.
/// </remarks>
public static class Mp4Info
{
    private const long MaxMoovBytes = 64 * 1024 * 1024;

    public static VideoInfo? TryRead(Stream file)
    {
        try
        {
            return Read(file);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static VideoInfo? Read(Stream file)
    {
        var header = new byte[16];
        string? brand = null;
        byte[]? moov = null;
        var sawMdat = false;
        var fastStart = false;

        file.Seek(0, SeekOrigin.Begin);
        while (file.Position + 8 <= file.Length)
        {
            var start = file.Position;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = file.Length - start; // fino alla fine del file
            }
            if (size < headerSize) return null;

            if (start == 0 && type != "ftyp") return null; // non è un MP4/MOV
            switch (type)
            {
                case "ftyp":
                    var brandBytes = new byte[4];
                    file.ReadExactly(brandBytes);
                    brand = Encoding.ASCII.GetString(brandBytes);
                    break;
                case "mdat":
                    sawMdat = true;
                    break;
                case "moov":
                    if (size - headerSize > MaxMoovBytes) return null;
                    fastStart = !sawMdat;
                    moov = new byte[size - headerSize];
                    file.ReadExactly(moov);
                    break;
            }

            if (moov is not null && sawMdat) break;
            file.Seek(start + size, SeekOrigin.Begin);
        }

        if (brand is null || moov is null) return null;

        var durationMs = 0;
        int width = 0, height = 0;
        foreach (var (type, box) in Boxes(moov))
        {
            if (type == "mvhd") durationMs = MovieDurationMs(box);
            if (type == "trak" && VideoTrackSize(box) is { } size) (width, height) = size;
        }

        if (width <= 0 || height <= 0 || durationMs <= 0) return null;
        return new VideoInfo(width, height, durationMs, fastStart, brand == "qt  " ? "video/quicktime" : "video/mp4");
    }

    /// <summary>Le box figlie dentro un contenitore già in memoria.</summary>
    private static IEnumerable<(string Type, byte[] Body)> Boxes(byte[] container)
    {
        var offset = 0;
        while (offset + 8 <= container.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(container.AsSpan(offset));
            var type = Encoding.ASCII.GetString(container, offset + 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(container.AsSpan(offset + 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = container.Length - offset;
            }
            if (size < headerSize || offset + size > container.Length) yield break;

            yield return (type, container[(offset + headerSize)..(int)(offset + size)]);
            offset += (int)size;
        }
    }

    private static int MovieDurationMs(byte[] mvhd)
    {
        var version = mvhd[0];
        uint timescale;
        ulong duration;
        if (version == 1)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20));
            duration = BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24));
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
            duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
        }
        return timescale == 0 ? 0 : (int)Math.Min(int.MaxValue, duration * 1000 / timescale);
    }

    /// <summary>Le dimensioni di visualizzazione, se è la traccia video (handler "vide").</summary>
    private static (int, int)? VideoTrackSize(byte[] trak)
    {
        byte[]? tkhd = null;
        var isVideo = false;
        foreach (var (type, box) in Boxes(trak))
        {
            if (type == "tkhd") tkhd = box;
            if (type == "mdia")
                foreach (var (inner, body) in Boxes(box))
                    if (inner == "hdlr" && body.Length >= 12 && Encoding.ASCII.GetString(body, 8, 4) == "vide") isVideo = true;
        }
        if (!isVideo || tkhd is null) return null;

        // Dopo i campi del tempo (più lunghi nella versione 1) vengono riservati,
        // layer, gruppo, volume, la matrice 3×3 e infine larghezza e altezza in 16.16.
        var matrix = tkhd[0] == 1 ? 52 : 40;
        var a = BinaryPrimitives.ReadInt32BigEndian(tkhd.AsSpan(matrix));
        var b = BinaryPrimitives.ReadInt32BigEndian(tkhd.AsSpan(matrix + 4));
        var width = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(matrix + 36)) >> 16);
        var height = (int)(BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(matrix + 40)) >> 16);

        // Ruotato di 90° o 270°: a = 0 e b = ±1. Un video verticale del telefono
        // è spesso salvato orizzontale con questa rotazione.
        return a == 0 && Math.Abs(b) == 0x10000 ? (height, width) : (width, height);
    }
}
