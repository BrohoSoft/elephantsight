using System.Text;
using Flarelytics.Core.Reports;
using Flarelytics.Tests.Integration.Infrastructure;
using static Flarelytics.Tests.Integration.Infrastructure.GoogleReports;

namespace Flarelytics.Tests.Unit;

public class GoogleInstallsTests
{
    private static readonly DateOnly Day = new(2026, 9, 3);

    [Fact]
    public void Legge_il_csv_utf16_e_usa_le_installazioni_per_utente()
    {
        var rows = GoogleInstallsParser.Parse(Guid.NewGuid(), "com.esempio.meteo", Csv("com.esempio.meteo",
            new Row(Day, "IT", UserInstalls: 7, Upgrades: 20, UserUninstalls: 2, DeviceInstalls: 9),
            new Row(Day, "DE", 3, 1, 0)));

        var it = rows.Single(r => r.CountryCode == "IT");
        Assert.Equal(7, it.Downloads);       // utenti nuovi, non dispositivi
        Assert.Equal(20, it.Updates);
        Assert.Equal(2, it.Uninstalls);
        Assert.Equal("com.esempio.meteo", it.AppId);
        Assert.Equal(3, rows.Single(r => r.CountryCode == "DE").Downloads);
    }

    [Fact]
    public void Con_i_nomi_di_colonna_piu_recenti_usa_gli_eventi()
    {
        // Un formato con le sole colonne "events": il parser deve ripiegare.
        var csv = "Date,Package Name,Country,Install events,Update events,Uninstall events\n2026-09-03,com.app,US,5,8,1\n";
        var row = Assert.Single(GoogleInstallsParser.Parse(Guid.NewGuid(), "com.app", Encoding.UTF8.GetBytes(csv)));

        Assert.Equal(5, row.Downloads);
        Assert.Equal(8, row.Updates);
        Assert.Equal(1, row.Uninstalls);
    }

    [Fact]
    public void Paese_vuoto_e_campi_tra_virgolette()
    {
        var csv = "Date,Package Name,Country,Daily User Installs\n2026-09-03,\"com.app\",,4\n2026-09-03,com.app,\"FR\",2\n";
        var rows = GoogleInstallsParser.Parse(Guid.NewGuid(), "com.app", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(4, rows.Single(r => r.CountryCode == "ZZ").Downloads);
        Assert.Equal(2, rows.Single(r => r.CountryCode == "FR").Downloads);
    }

    [Theory]
    [InlineData("stats/installs/installs_com.azienda.mia_app_202609_country.csv", "com.azienda.mia_app", 2026, 9)]
    [InlineData("stats/installs/installs_com.app_202512_country.csv", "com.app", 2025, 12)]
    public void Package_e_mese_dal_nome_del_file(string name, string package, int year, int month)
    {
        var parsed = GoogleInstallsParser.ParseName(name);
        Assert.Equal((package, new DateOnly(year, month, 1)), parsed);
    }

    [Theory]
    [InlineData("stats/installs/installs_com.app_202609_overview.csv")]
    [InlineData("stats/installs/installs_com.app_202609_device.csv")]
    [InlineData("stats/ratings/ratings_com.app_202609_country.csv")]
    public void Gli_altri_file_si_ignorano(string name) => Assert.Null(GoogleInstallsParser.ParseName(name));

    [Fact]
    public void Senza_le_colonne_essenziali_si_ferma()
    {
        Assert.Throws<FormatException>(() =>
            GoogleInstallsParser.Parse(Guid.NewGuid(), "com.app", Encoding.UTF8.GetBytes("Date,Package Name\n2026-09-03,com.app\n")));
    }
}
