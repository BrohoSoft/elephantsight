using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Reports;

/// <summary>Importi di un giorno, paese e app, nella valuta in cui li dà Google.</summary>
public record GoogleMoneyRow(DateOnly Date, string Country, string Package, string Currency, decimal Amount, int Purchases, int Refunds);

/// <summary>
/// I report finanziari di Google Play: vendite (lordo, ordine per ordine) e
/// guadagni (netto, transazione per transazione).
/// </summary>
/// <remarks>
/// <para>Sono CSV dentro uno zip. Come per gli altri report, le colonne si
/// leggono per nome e con nomi alternativi: Google li ha cambiati nel tempo
/// ("Product id" è diventato "Package ID", i tipi di prodotto da "inapp" a
/// "One-time product").</para>
///
/// <para><b>Da verificare sui file veri.</b> La documentazione non dice il
/// segno degli importi nei rimborsi né se un rimborso è una riga a sé o un
/// cambio di stato dell'ordine. Il parser non si fida del segno: il verso lo
/// decide il tipo di riga (rimborso = sottrae, sempre), e l'importo si prende
/// in valore assoluto. Nei guadagni invece il segno è parte del dato
/// (commissioni e tasse sono negative) e si somma così com'è.</para>
/// </remarks>
public static partial class GoogleFinanceParser
{
    public const int Version = 1;

    public static (string Kind, DateOnly Month)? ParseName(string objectName)
    {
        var sales = SalesFile().Match(objectName);
        if (sales.Success) return ("sales", Month(sales.Groups[1].Value));

        var earnings = EarningsFile().Match(objectName);
        if (earnings.Success) return ("earnings", Month(earnings.Groups[1].Value));

        return null;
    }

    /// <summary>
    /// Report vendite: venduto lordo (tasse incluse, come il "Customer Price"
    /// di Apple), acquisti e rimborsi per giorno UTC, paese e app.
    /// </summary>
    public static List<GoogleMoneyRow> ParseSales(byte[] zip)
    {
        var (header, rows) = ReadZippedCsv(zip);
        int date = Col(header, "Order Charged Date"), status = Col(header, "Financial Status"),
            type = Col(header, "Product Type"), package = Col(header, "Package ID", "Product ID"),
            currency = Col(header, "Currency of Sale"), amount = Col(header, "Charged Amount"),
            country = Col(header, "Country of Buyer");

        var result = new List<GoogleMoneyRow>();
        foreach (var f in rows)
        {
            var financialStatus = f[status].Trim().ToLowerInvariant();
            var isRefund = financialStatus.Contains("refund");
            var isCharge = financialStatus.StartsWith("charged");
            if (!isRefund && !isCharge) continue; // ordini annullati, in attesa, rifiutati: nessun movimento

            var value = Math.Abs(Decimal(f[amount]));
            var isPaidApp = f[type].Trim().ToLowerInvariant() is "paid app" or "paidapp";

            result.Add(new GoogleMoneyRow(
                DateOnly.ParseExact(f[date].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Country(f[country]),
                f[package].Trim(),
                f[currency].Trim().ToUpperInvariant(),
                isRefund ? -value : value,
                // Un'app a pagamento è già un download nei report delle
                // installazioni: qui si contano solo gli acquisti in-app.
                isCharge && !isPaidApp ? 1 : 0,
                // Un rimborso parziale riduce l'importo ma non è un ordine rimborsato.
                financialStatus == "refund" || financialStatus == "refunded" ? 1 : 0));
        }

        return result;
    }

    /// <summary>
    /// Report guadagni: il netto. Ogni riga è un pezzo di una transazione
    /// (incasso, commissione Google, tasse, rimborso…): sommandole tutte per
    /// giorno, paese e app si ottiene quello che Google paga.
    /// </summary>
    public static List<GoogleMoneyRow> ParseEarnings(byte[] zip)
    {
        var (header, rows) = ReadZippedCsv(zip);
        int date = Col(header, "Transaction Date"), package = Col(header, "Package ID", "Product id"),
            currency = Col(header, "Merchant Currency"), amount = Col(header, "Amount (Merchant Currency)"),
            country = Col(header, "Buyer Country");

        var result = new List<GoogleMoneyRow>();
        foreach (var f in rows)
        {
            var app = f[package].Trim();
            if (app.Length == 0) continue; // rettifiche dell'account, non legate a un'app

            result.Add(new GoogleMoneyRow(
                // Giorno del Pacifico, scritto all'americana: "Nov 30, 2016".
                DateOnly.ParseExact(f[date].Trim(), ["MMM d, yyyy", "MMM dd, yyyy"], CultureInfo.InvariantCulture),
                Country(f[country]),
                app,
                f[currency].Trim().ToUpperInvariant(),
                Decimal(f[amount]),
                0, 0));
        }

        return result;
    }

    private static (string[] Header, List<string[]> Rows) ReadZippedCsv(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("Nello zip di Google non c'è un CSV.");

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = Csv.Split(reader.ReadLine() ?? throw new FormatException("Il report di Google è vuoto."));

        var rows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = Csv.Split(line);
            if (fields.Length < header.Length) Array.Resize(ref fields, header.Length);
            rows.Add(fields.Select(x => x ?? "").ToArray());
        }

        return (header, rows);
    }

    private static int Col(string[] header, params string[] names)
    {
        foreach (var name in names)
        {
            var i = Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        throw new FormatException($"Nel report di Google manca la colonna \"{names[0]}\".");
    }

    private static string Country(string value)
    {
        var code = value.Trim().ToUpperInvariant();
        return code.Length == 2 ? code : "ZZ";
    }

    private static decimal Decimal(string value) =>
        decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static DateOnly Month(string yyyymm) =>
        DateOnly.ParseExact(yyyymm + "01", "yyyyMMdd", CultureInfo.InvariantCulture);

    // salesreport_202609.zip; i guadagni a volte hanno un suffisso con l'id
    // dello sviluppatore e un progressivo (earnings_202609_1234-1.zip).
    [GeneratedRegex(@"^sales/salesreport_(\d{6})(_[^/]*)?\.zip$")]
    private static partial Regex SalesFile();

    [GeneratedRegex(@"^earnings/earnings_(\d{6})(_[^/]*)?\.zip$")]
    private static partial Regex EarningsFile();
}

/// <summary>Il CSV di Google: virgole, e campi tra virgolette che possono contenerne.</summary>
public static class Csv
{
    public static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }
}
