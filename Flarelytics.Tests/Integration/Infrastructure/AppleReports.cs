using System.IO.Compression;
using System.Text;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>Report di vendita di Apple finti ma nel formato vero: TSV con intestazione, compresso in gzip.</summary>
public static class AppleReports
{
    /// <summary>
    /// Le colonne della versione 1_0, più una colonna in fondo che nella 1_0
    /// non c'è: il parser deve leggere per nome e ignorare quello che non conosce.
    /// </summary>
    private static readonly string[] Header =
    [
        "Provider", "Provider Country", "SKU", "Developer", "Title", "Version", "Product Type Identifier", "Units",
        "Developer Proceeds", "Begin Date", "End Date", "Customer Currency", "Country Code", "Currency of Proceeds",
        "Apple Identifier", "Customer Price", "Promo Code", "Parent Identifier", "Subscription", "Period", "Category",
        "CMB", "Device", "Supported Platforms", "Proceeds Reason", "Preserved Pricing", "Client", "Order Type", "Colonna Futura"
    ];

    public record Row(string Sku, string Type, int Units, decimal Proceeds, string CustomerCurrency, string Country,
        string ProceedsCurrency, string AppleId, decimal Price, string Parent = "");

    public static byte[] Gzip(DateOnly date, params Row[] rows)
    {
        var text = new StringBuilder(string.Join('\t', Header)).Append('\n');
        foreach (var r in rows)
        {
            string[] f =
            [
                "APPLE", "US", r.Sku, "Esempio", "App", "1.0", r.Type, r.Units.ToString(), r.Proceeds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{date:MM/dd/yyyy}", $"{date:MM/dd/yyyy}", r.CustomerCurrency, r.Country, r.ProceedsCurrency, r.AppleId,
                r.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), "", r.Parent, "", "", "Weather", "", "iPhone", "iOS", "", "", "", "", "x"
            ];
            text.Append(string.Join('\t', f)).Append('\n');
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text.ToString()));
        }
        return output.ToArray();
    }
}
