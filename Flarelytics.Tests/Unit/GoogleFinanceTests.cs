using System.Text;
using Flarelytics.Core.Reports;
using static Flarelytics.Tests.Integration.Infrastructure.GoogleReports;

namespace Flarelytics.Tests.Unit;

public class GoogleFinanceTests
{
    private static readonly DateOnly Day = new(2026, 9, 3);

    [Fact]
    public void Vendite_incassi_rimborsi_e_annullati()
    {
        var csv = string.Join('\n',
            SalesHeader,
            SalesRow(Day, "Charged", "One-time product", "com.app", "EUR", 4.99m, "IT"),
            SalesRow(Day, "Charged", "Subscription", "com.app", "EUR", 9.99m, "IT"),
            SalesRow(Day, "Refund", "One-time product", "com.app", "EUR", 4.99m, "IT"),
            SalesRow(Day, "Partial refund", "Subscription", "com.app", "EUR", -2.00m, "IT"),
            SalesRow(Day, "Charged", "Paid app", "com.app", "EUR", 1.99m, "IT"),
            SalesRow(Day, "Cancelled", "One-time product", "com.app", "EUR", 4.99m, "IT"));

        var rows = GoogleFinanceParser.ParseSales(Zip("salesreport_202609.csv", csv));

        Assert.Equal(2, rows.Sum(r => r.Purchases));                     // l'app a pagamento è un download, non un acquisto
        Assert.Equal(1, rows.Sum(r => r.Refunds));                       // il parziale riduce l'importo, non conta come rimborso
        Assert.Equal(4.99m + 9.99m - 4.99m - 2.00m + 1.99m, rows.Sum(r => r.Amount)); // il segno lo dà lo stato, non il numero
        Assert.DoesNotContain(rows, r => r.Amount == 4.99m && r.Purchases == 0 && r.Refunds == 0); // l'annullato non c'è
    }

    [Fact]
    public void Guadagni_sommano_incasso_commissione_e_tasse_con_il_loro_segno()
    {
        var csv = string.Join('\n',
            EarningsHeader,
            EarningsRow(Day, "Charge", "com.app", "IT", "EUR", 9.99m),
            EarningsRow(Day, "Google fee", "com.app", "IT", "EUR", -1.50m),
            EarningsRow(Day, "Tax", "com.app", "IT", "EUR", -1.80m),
            EarningsRow(Day, "Charge refund", "com.app", "IT", "EUR", -4.99m),
            EarningsRow(Day, "Adjustment", "", "", "EUR", 100m));

        var rows = GoogleFinanceParser.ParseEarnings(Zip("earnings.csv", csv));

        Assert.Equal(9.99m - 1.50m - 1.80m - 4.99m, rows.Sum(r => r.Amount));
        Assert.All(rows, r => Assert.Equal(Day, r.Date));               // "Sep 3, 2026"
        Assert.DoesNotContain(rows, r => r.Package == "");               // la rettifica senza app si salta
    }

    [Fact]
    public void Il_vecchio_nome_della_colonna_del_package_funziona_ancora()
    {
        var csv = "Description,Transaction Date,Transaction Type,Product id,Buyer Country,Merchant Currency,Amount (Merchant Currency)\nx,\"Sep 3, 2026\",Charge,com.app,DE,USD,5.00\n";
        var row = Assert.Single(GoogleFinanceParser.ParseEarnings(Zip("e.csv", csv)));

        Assert.Equal(("com.app", "DE", "USD", 5.00m), (row.Package, row.Country, row.Currency, row.Amount));
    }

    [Theory]
    [InlineData("sales/salesreport_202609.zip", "sales", 2026, 9)]
    [InlineData("earnings/earnings_202608_1234567890-1.zip", "earnings", 2026, 8)]
    [InlineData("earnings/earnings_202607.zip", "earnings", 2026, 7)]
    public void Tipo_e_mese_dal_nome(string name, string kind, int year, int month) =>
        Assert.Equal((kind, new DateOnly(year, month, 1)), GoogleFinanceParser.ParseName(name));

    [Theory]
    [InlineData("sales/salesreport_202609.csv")]
    [InlineData("earnings/sub/earnings_202609.zip")]
    [InlineData("stats/installs/installs_com.app_202609_country.csv")]
    public void Gli_altri_file_si_ignorano(string name) => Assert.Null(GoogleFinanceParser.ParseName(name));

    [Fact]
    public void Uno_zip_senza_csv_si_ferma()
    {
        using var output = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(archive.CreateEntry("leggimi.txt").Open());
            w.Write("niente");
        }
        Assert.Throws<FormatException>(() => GoogleFinanceParser.ParseSales(output.ToArray()));
    }
}
