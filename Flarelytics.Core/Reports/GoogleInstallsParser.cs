using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Reports;

/// <summary>
/// Legge il report mensile delle installazioni di Google Play per paese
/// (<c>stats/installs/installs_&lt;package&gt;_&lt;aaaamm&gt;_country.csv</c>).
/// </summary>
/// <remarks>
/// <para><b>UTF-16.</b> Google scrive questi CSV in UTF-16 con BOM; il lettore
/// riconosce la codifica dal BOM e ripiega su UTF-8 se non c'è.</para>
///
/// <para><b>Colonne per nome, con alternative.</b> Google ha cambiato i nomi
/// nel tempo ("Daily Device Upgrades" e "Update events", per esempio). Per
/// ogni metrica c'è un elenco di nomi in ordine di preferenza: si usa il primo
/// che il file contiene.</para>
///
/// <para><b>Quali numeri.</b> Download = "Daily User Installs": utenti che
/// installano l'app per la prima volta su un qualunque dispositivo, la
/// definizione più vicina ai download di Apple. Le installazioni per
/// dispositivo contano anche i telefoni nuovi di chi l'aveva già.</para>
/// </remarks>
public static partial class GoogleInstallsParser
{
    /// <summary>Quando cambia il modo di contare, si alza: i file già salvati vengono rielaborati.</summary>
    public const int Version = 1;

    private static readonly string[] DownloadColumns = ["Daily User Installs", "Daily Device Installs", "Install events"];
    private static readonly string[] UpdateColumns = ["Daily Device Upgrades", "Update events"];
    private static readonly string[] UninstallColumns = ["Daily User Uninstalls", "Daily Device Uninstalls", "Uninstall events"];

    /// <summary>Il prefisso nel bucket sotto cui cercare i file delle installazioni.</summary>
    public const string Prefix = "stats/installs/";

    /// <summary>
    /// Package e mese dal nome del file, o null se non è un file per paese.
    /// Il package può contenere trattini bassi (<c>com.azienda.mia_app</c>):
    /// il mese si riconosce perché sono le ultime sei cifre prima di
    /// <c>_country</c>.
    /// </summary>
    public static (string Package, DateOnly Month)? ParseName(string objectName)
    {
        var match = CountryFile().Match(objectName);
        if (!match.Success) return null;

        var month = DateOnly.ParseExact(match.Groups[2].Value + "01", "yyyyMMdd", CultureInfo.InvariantCulture);
        return (match.Groups[1].Value, month);
    }

    public static List<DailyAppMetric> Parse(Guid tenantId, string packageName, byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var header = Csv.Split(reader.ReadLine() ?? throw new FormatException("Il report di Google è vuoto."));

        int Find(params string[] names)
        {
            foreach (var name in names)
            {
                var i = Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        int date = Find("Date"), country = Find("Country"), downloads = Find(DownloadColumns),
            updates = Find(UpdateColumns), uninstalls = Find(UninstallColumns);

        if (date < 0 || country < 0 || downloads < 0)
        {
            throw new FormatException("Nel report delle installazioni di Google mancano le colonne Date, Country o le installazioni giornaliere.");
        }

        var byKey = new Dictionary<(DateOnly, string), DailyAppMetric>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = Csv.Split(line);

            var day = DateOnly.ParseExact(f[date].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            // Google lascia vuoto il paese quando non lo conosce: si tiene la
            // riga sotto "ZZ" (codice ISO per "sconosciuto") invece di perderla.
            var code = f[country].Trim().ToUpperInvariant();
            if (code.Length != 2) code = "ZZ";

            if (!byKey.TryGetValue((day, code), out var m))
            {
                m = new DailyAppMetric { TenantId = tenantId, Store = Store.GooglePlay, AppId = packageName, Date = day, CountryCode = code };
                byKey[(day, code)] = m;
            }

            m.Downloads += Int(f, downloads);
            m.Updates += Int(f, updates);
            m.Uninstalls += Int(f, uninstalls);
        }

        return byKey.Values.ToList();
    }

    private static int Int(string[] fields, int index) =>
        index >= 0 && index < fields.Length && int.TryParse(fields[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;

    [GeneratedRegex(@"^stats/installs/installs_(.+)_(\d{6})_country\.csv$")]
    private static partial Regex CountryFile();
}
