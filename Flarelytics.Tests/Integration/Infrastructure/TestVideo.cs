using System.Buffers.Binary;
using System.Text;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// Un MP4 minimo: ftyp, moov (mvhd + una traccia video con tkhd e hdlr "vide")
/// e un mdat finto. Basta per quello che il server legge, ed è piccolo.
/// </summary>
public static class TestVideo
{
    /// <param name="fastStart">moov prima di mdat (come vuole Instagram) o dopo.</param>
    /// <param name="rotated">Salvato orizzontale con la matrice ruotata di 90°, come i video verticali dei telefoni.</param>
    /// <param name="padding">Byte di "video" nel mdat, per fare file più grandi.</param>
    public static byte[] Create(int width, int height, int durationMs, bool fastStart = true, bool rotated = false, int padding = 1000, string brand = "isom")
    {
        var ftyp = Box("ftyp", [.. Encoding.ASCII.GetBytes(brand), 0, 0, 2, 0, .. Encoding.ASCII.GetBytes("isommp42")]);

        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);              // timescale
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)durationMs);  // durata

        var tkhd = new byte[84];
        var matrix = 40;
        if (rotated)
        {
            BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(matrix + 4), 0x10000);  // b
            BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(matrix + 12), -0x10000); // c
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(matrix), 0x10000);       // a
            BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(matrix + 16), 0x10000);  // d
        }
        // Ruotato: si salva con le dimensioni scambiate, il lettore deve rigirarle.
        var (w, h) = rotated ? (height, width) : (width, height);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(matrix + 36), (uint)w << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(matrix + 40), (uint)h << 16);

        var hdlr = new byte[24];
        Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);

        var moov = Box("moov", [.. Box("mvhd", mvhd), .. Box("trak", [.. Box("tkhd", tkhd), .. Box("mdia", Box("hdlr", hdlr))])]);
        var mdat = Box("mdat", new byte[padding]);

        return fastStart ? [.. ftyp, .. moov, .. mdat] : [.. ftyp, .. mdat, .. moov];
    }

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }
}
