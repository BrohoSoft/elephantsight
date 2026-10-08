using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Flarelytics.Core.Management;

/// <summary>I dati di un .ipa che servono ad App Store Connect per accettarlo.</summary>
public record IpaInfo(string BundleId, string Version, string BuildNumber);

/// <summary>
/// Legge <c>Payload/*.app/Info.plist</c> da un .ipa (che è uno zip).
/// </summary>
/// <remarks>
/// Xcode scrive l'Info.plist in formato <b>binario</b> (<c>bplist00</c>), non
/// XML: il lettore li capisce entrambi. Del formato binario si legge solo
/// quello che serve qui, cioè un dizionario con valori stringa.
/// </remarks>
public static partial class IpaReader
{
    public static IpaInfo Read(Stream ipa)
    {
        using var zip = new ZipArchive(ipa, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(e => InfoPlist().IsMatch(e.FullName))
            ?? throw new FormatException("Il file non è un .ipa: manca Payload/<App>.app/Info.plist.");

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var dict = ParsePlist(buffer.ToArray());

        string Get(string key) => dict.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new FormatException($"Nell'Info.plist manca {key}.");

        return new IpaInfo(Get("CFBundleIdentifier"), Get("CFBundleShortVersionString"), Get("CFBundleVersion"));
    }

    public static Dictionary<string, string> ParsePlist(byte[] data) =>
        data.AsSpan().StartsWith("bplist00"u8) ? ParseBinary(data) : ParseXml(data);

    private static Dictionary<string, string> ParseXml(byte[] data)
    {
        var dict = XDocument.Parse(Encoding.UTF8.GetString(data)).Root?.Element("dict")
            ?? throw new FormatException("Info.plist senza dizionario.");

        var result = new Dictionary<string, string>();
        var items = dict.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            if (items[i].Name == "key" && items[i + 1].Name == "string") result[items[i].Value] = items[i + 1].Value;
        }
        return result;
    }

    /// <summary>
    /// Il formato binario: in coda 32 byte di "trailer" con la dimensione degli
    /// offset e dei riferimenti, il numero di oggetti, l'oggetto radice e dove
    /// sta la tabella degli offset. Ogni oggetto comincia con un byte: tipo
    /// nei 4 bit alti, lunghezza nei 4 bassi (0xF = la lunghezza segue come intero).
    /// </summary>
    private static Dictionary<string, string> ParseBinary(byte[] data)
    {
        var trailer = data.AsSpan(data.Length - 32);
        int offsetSize = trailer[6], refSize = trailer[7];
        var count = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var top = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var tableOffset = (int)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        long ReadInt(int position, int size)
        {
            long value = 0;
            for (var i = 0; i < size; i++) value = (value << 8) | data[position + i];
            return value;
        }

        int Offset(int obj) => (int)ReadInt(tableOffset + obj * offsetSize, offsetSize);

        (int Length, int Start) Length(int position)
        {
            var marker = data[position];
            var length = marker & 0x0F;
            if (length != 0x0F) return (length, position + 1);

            var intMarker = data[position + 1];
            var size = 1 << (intMarker & 0x0F);
            return ((int)ReadInt(position + 2, size), position + 2 + size);
        }

        string? String(int obj)
        {
            var position = Offset(obj);
            var type = data[position] >> 4;
            var (length, start) = Length(position);
            return type switch
            {
                0x5 => Encoding.ASCII.GetString(data, start, length),
                0x6 => Encoding.BigEndianUnicode.GetString(data, start, length * 2),
                _ => null
            };
        }

        var root = Offset(top);
        if (data[root] >> 4 != 0xD) throw new FormatException("L'Info.plist binario non comincia con un dizionario.");

        var (entries, first) = Length(root);
        var result = new Dictionary<string, string>();
        for (var i = 0; i < entries; i++)
        {
            var keyRef = (int)ReadInt(first + i * refSize, refSize);
            var valueRef = (int)ReadInt(first + (entries + i) * refSize, refSize);
            if (keyRef >= count || valueRef >= count) continue;

            if (String(keyRef) is { } key && String(valueRef) is { } value) result[key] = value;
        }
        return result;
    }

    [GeneratedRegex(@"^Payload/[^/]+\.app/Info\.plist$")]
    private static partial Regex InfoPlist();
}
