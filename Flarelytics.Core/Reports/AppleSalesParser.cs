using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Flarelytics.Core.Reports;

/// <summary>Una riga del report Sales Summary di Apple, con le sole colonne che servono.</summary>
public record AppleSalesRow(
    string Sku,
    string ProductType,
    int Units,
    decimal DeveloperProceeds,
    string CustomerCurrency,
    string CountryCode,
    string ProceedsCurrency,
    string AppleIdentifier,
    decimal CustomerPrice,
    string ParentIdentifier);

/// <summary>
/// Legge il report di vendita giornaliero di Apple: un TSV compresso in gzip,
/// con una riga d'intestazione.
/// </summary>
/// <remarks>
/// Le colonne si trovano per nome, non per posizione: Apple ne ha aggiunte nel
/// tempo (la versione 1_3 del report nell'interfaccia ne ha più della 1_0
/// dell'API), e un parser per posizione si romperebbe in silenzio, leggendo
/// numeri dalla colonna sbagliata.
/// </remarks>
public static class AppleSalesParser
{
    public static List<AppleSalesRow> Parse(byte[] gzip)
    {
        using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);

        var header = reader.ReadLine()?.Split('\t')
            ?? throw new FormatException("Il report di Apple è vuoto.");

        int Column(string name) => Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
            ? i
            : throw new FormatException($"Nel report di Apple manca la colonna \"{name}\".");

        int sku = Column("SKU"), type = Column("Product Type Identifier"), units = Column("Units"),
            proceeds = Column("Developer Proceeds"), customerCurrency = Column("Customer Currency"),
            country = Column("Country Code"), proceedsCurrency = Column("Currency of Proceeds"),
            appleId = Column("Apple Identifier"), price = Column("Customer Price"), parent = Column("Parent Identifier");

        var rows = new List<AppleSalesRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split('\t');

            rows.Add(new AppleSalesRow(
                f[sku].Trim(),
                f[type].Trim(),
                int.Parse(f[units], NumberStyles.Integer | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                Decimal(f[proceeds]),
                f[customerCurrency].Trim().ToUpperInvariant(),
                f[country].Trim().ToUpperInvariant(),
                f[proceedsCurrency].Trim().ToUpperInvariant(),
                f[appleId].Trim(),
                Decimal(f[price]),
                f[parent].Trim()));
        }

        return rows;
    }

    private static decimal Decimal(string value) =>
        string.IsNullOrWhiteSpace(value) ? 0m : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
