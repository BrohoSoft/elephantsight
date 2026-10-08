using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Tests.Integration.Infrastructure;
using static Flarelytics.Tests.Integration.Infrastructure.AppleReports;

namespace Flarelytics.Tests.Unit;

public class AppleSalesTests
{
    private static readonly DateOnly Day = new(2026, 9, 15);
    private const string AppId = "1234567890";

    private static readonly CurrencyConverter Rates = CurrencyConverter.FromRates(
    [
        new ExchangeRate { Date = Day, Currency = "USD", UnitsPerEuro = 1.25m }
    ]);

    private static (List<DailyAppMetric> Metrics, int Unattributed) Aggregate(params Row[] rows) =>
        AppleSalesAggregator.Aggregate(Guid.NewGuid(), Day, AppleSalesParser.Parse(Gzip(Day, rows)),
            new Dictionary<string, string> { ["APP1"] = AppId },
            (amount, currency) => Rates.ToEurMicros(amount, currency, Day));

    [Fact]
    public void Download_riscaricamento_e_aggiornamento_si_contano_separati()
    {
        var (metrics, _) = Aggregate(
            new Row("APP1", "1F", 10, 0, "EUR", "IT", "EUR", AppId, 0),
            new Row("APP1", "1T", 2, 0, "EUR", "IT", "EUR", AppId, 0),
            new Row("APP1", "3F", 4, 0, "EUR", "IT", "EUR", AppId, 0),
            new Row("APP1", "7F", 50, 0, "EUR", "IT", "EUR", AppId, 0));

        var it = Assert.Single(metrics);
        Assert.Equal(12, it.Downloads);
        Assert.Equal(4, it.Redownloads);
        Assert.Equal(50, it.Updates);
        Assert.Equal(0, it.ProceedsEurMicros);
    }

    [Fact]
    public void I_ricavi_sono_unita_per_ricavo_unitario_e_i_rimborsi_li_tolgono()
    {
        // Nei rimborsi Apple scrive il prezzo negativo ma il ricavo positivo:
        // il segno deve venire dalle unità, una volta sola.
        var (metrics, _) = Aggregate(
            new Row("APP1", "1", 3, 1.40m, "EUR", "IT", "EUR", AppId, 1.99m),
            new Row("APP1", "1", -1, 1.40m, "EUR", "IT", "EUR", AppId, -1.99m));

        var it = Assert.Single(metrics);
        Assert.Equal(3, it.Downloads);
        Assert.Equal(1, it.Refunds);
        Assert.Equal(2_800_000, it.ProceedsEurMicros);   // (3 − 1) × 1,40
        Assert.Equal(3_980_000, it.SalesEurMicros);      // (3 − 1) × 1,99
    }

    [Fact]
    public void Gli_acquisti_in_app_vanno_all_app_del_loro_sku_e_si_convertono_in_euro()
    {
        var (metrics, unattributed) = Aggregate(
            new Row("COINS100", "IA1", 4, 3.50m, "USD", "US", "USD", "9999999999", 4.99m, Parent: "APP1"),
            new Row("SUB_M", "IAY", 1, 7.00m, "USD", "US", "USD", "8888888888", 9.99m, Parent: "APP1"),
            new Row("RESTORE", "IA3", 2, 0m, "USD", "US", "USD", "7777777777", 0m, Parent: "APP1"),
            new Row("ORFANO", "IA1", 1, 1m, "USD", "US", "USD", "6666666666", 1m, Parent: "SCONOSCIUTA"));

        var us = Assert.Single(metrics);
        Assert.Equal(AppId, us.AppId);
        Assert.Equal(5, us.InAppPurchases);              // IA3 è un ripristino, non un acquisto
        Assert.Equal(16_800_000, us.ProceedsEurMicros);  // (4 × 3,50 + 7,00) / 1,25
        Assert.Equal(1, unattributed);
    }

    [Fact]
    public void Una_valuta_senza_cambio_bce_non_finisce_nei_totali_ma_si_segnala()
    {
        var (metrics, _) = Aggregate(new Row("APP1", "1", 1, 100m, "XYZ", "ZZ", "XYZ", AppId, 120m));

        var row = Assert.Single(metrics);
        Assert.Equal(1, row.Downloads);
        Assert.Equal(0, row.ProceedsEurMicros);
        Assert.True(row.HasUnconvertedAmounts);
    }

    [Fact]
    public void Un_paese_per_riga()
    {
        var (metrics, _) = Aggregate(
            new Row("APP1", "1F", 1, 0, "EUR", "IT", "EUR", AppId, 0),
            new Row("APP1", "1F", 2, 0, "EUR", "DE", "EUR", AppId, 0),
            new Row("APP1", "1F", 3, 0, "EUR", "IT", "EUR", AppId, 0));

        Assert.Equal(4, metrics.Single(m => m.CountryCode == "IT").Downloads);
        Assert.Equal(2, metrics.Single(m => m.CountryCode == "DE").Downloads);
    }

    [Fact]
    public void Una_colonna_che_manca_ferma_il_parser_invece_di_leggere_numeri_sbagliati()
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
        {
            gzip.Write("SKU\tUnits\nAPP1\t3\n"u8);
        }

        Assert.Throws<FormatException>(() => AppleSalesParser.Parse(output.ToArray()));
    }
}

public class ExchangeRateTests
{
    [Fact]
    public void Nel_fine_settimana_vale_il_cambio_del_venerdi()
    {
        var friday = new DateOnly(2026, 10, 2);
        var converter = CurrencyConverter.FromRates([new ExchangeRate { Date = friday, Currency = "USD", UnitsPerEuro = 1.10m }]);

        Assert.Equal(1_000_000, converter.ToEurMicros(1.10m, "USD", friday.AddDays(2)));
        Assert.Null(converter.ToEurMicros(1m, "USD", friday.AddDays(10)));
        Assert.Equal(5_000_000, converter.ToEurMicros(5m, "EUR", friday));
    }

    [Fact]
    public void Il_csv_della_bce_salta_i_valori_mancanti()
    {
        var rates = EcbExchangeRates.ParseCsv(
            "Date,USD,JPY,CYP,\n2026-10-02,1.1012,162.5,N/A,\n2026-10-01,1.0990,161.9,N/A,\n2025-01-01,1.0,1.0,N/A,\n",
            since: new DateOnly(2026, 1, 1));

        Assert.Equal(4, rates.Count);
        Assert.Equal(1.1012m, rates.Single(r => r.Currency == "USD" && r.Date == new DateOnly(2026, 10, 2)).UnitsPerEuro);
        Assert.DoesNotContain(rates, r => r.Currency == "CYP");
    }
}
