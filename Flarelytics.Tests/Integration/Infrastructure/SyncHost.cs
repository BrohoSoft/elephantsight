using Flarelytics.Core;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// Il worker, senza il suo ciclo: gli stessi servizi registrati allo stesso
/// modo, sullo stesso database e sulle stesse chiavi dell'API dei test, con un
/// finto Apple al posto di quello vero.
/// </summary>
public sealed class SyncHost : IAsyncDisposable
{
    public ServiceProvider Services { get; }
    public FakeAppleSalesReports Apple { get; } = new();
    public FakeGooglePlayReports Google { get; } = new();
    public FakeAppIconSource Icons { get; } = new();
    public string ReportsDirectory { get; }

    public SyncHost(FlarelyticsAppFactory app, string connectionString, int backfillDays = 10)
    {
        ReportsDirectory = Path.Combine(app.Root, "reports");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = connectionString,
            ["Secrets:KeysDirectory"] = app.KeysDirectory,
            ["Secrets:ActiveKeyVersion"] = "v1",
            ["Secrets:StorageDirectory"] = app.SecretsDirectory,
            ["Reports:StorageDirectory"] = ReportsDirectory,
            ["Sync:RequestDelay"] = "00:00:00",
            ["Sync:BackfillDays"] = backfillDays.ToString()
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddFlarelyticsDatabase(configuration);
        services.AddFlarelyticsSecretsAndStores(configuration, createDevelopmentKey: false);
        services.AddFlarelyticsSync();
        services.RemoveAll<IAppleSalesReports>();
        services.AddSingleton<IAppleSalesReports>(Apple);
        services.RemoveAll<IGooglePlayReports>();
        services.AddSingleton<IGooglePlayReports>(Google);
        services.RemoveAll<IAppIconSource>();
        services.AddSingleton<IAppIconSource>(Icons);

        Services = services.BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>Un Apple che ha i report che gli si danno, e conta le richieste.</summary>
public class FakeAppleSalesReports : IAppleSalesReports
{
    public Dictionary<DateOnly, byte[]> Reports { get; } = [];
    public List<(string Sku, string AppleId)> Skus { get; } = [];
    public List<DateOnly> Requested { get; } = [];
    public AppleFetchOutcome? ForceOutcome { get; set; }

    public Task<AppleFetchResult> FetchDailySalesAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, DateOnly date, CancellationToken ct)
    {
        Requested.Add(date);
        if (ForceOutcome is { } forced) return Task.FromResult(new AppleFetchResult(forced, Message: "forzato"));

        return Task.FromResult(Reports.TryGetValue(date, out var gz)
            ? new AppleFetchResult(AppleFetchOutcome.Report, gz)
            : new AppleFetchResult(AppleFetchOutcome.NoReport));
    }

    public Task<IReadOnlyList<(string Sku, string AppleId)>> ListAppSkusAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, string)>>(Skus);
}

/// <summary>Un bucket di Play Console in memoria: nome del file → contenuto.</summary>
public class FakeGooglePlayReports : IGooglePlayReports
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Downloaded { get; } = [];
    public GoogleAccessException? Fail { get; set; }

    public Task<string> ConnectAsync(ReadOnlyMemory<byte> secret, CancellationToken ct) =>
        Fail?.Problem == GoogleAccessProblem.InvalidKey ? throw Fail : Task.FromResult("token-finto");

    public Task<IReadOnlyList<BucketObject>> ListAsync(string token, string bucket, string prefix, CancellationToken ct)
    {
        if (Fail is not null) throw Fail;
        return Task.FromResult<IReadOnlyList<BucketObject>>(Files
            .Where(f => f.Key.StartsWith(prefix))
            .Select(f => new BucketObject(f.Key, Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(f.Value)), f.Value.Length))
            .ToList());
    }

    public Task<byte[]> DownloadAsync(string token, string bucket, string objectName, CancellationToken ct)
    {
        Downloaded.Add(objectName);
        return Task.FromResult(Files[objectName]);
    }
}

/// <summary>I CSV delle installazioni di Google come li scrive Google: UTF-16 con BOM.</summary>
public static class GoogleReports
{
    public const string Header = "Date,Package Name,Country,Daily Device Installs,Daily Device Uninstalls,Daily Device Upgrades,Total User Installs,Daily User Installs,Daily User Uninstalls,Active Device Installs,Install events,Update events,Uninstall events";

    public record Row(DateOnly Date, string Country, int UserInstalls, int Upgrades, int UserUninstalls, int DeviceInstalls = 0);

    public static byte[] Csv(string package, params Row[] rows)
    {
        var text = new System.Text.StringBuilder(Header).Append("\r\n");
        foreach (var r in rows)
        {
            text.Append($"{r.Date:yyyy-MM-dd},{package},{r.Country},{r.DeviceInstalls},0,{r.Upgrades},1000,{r.UserInstalls},{r.UserUninstalls},500,0,0,0\r\n");
        }
        return [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(text.ToString())];
    }

    public static string Name(string package, DateOnly month) => $"stats/installs/installs_{package}_{month:yyyyMM}_country.csv";
}

/// <summary>Icone finte: un PNG minuscolo per le app che si conoscono, null per le altre.</summary>
public class FakeAppIconSource : IAppIconSource
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    public HashSet<string> Known { get; } = [];
    public List<(Store Store, string AppId, IReadOnlyList<string> Countries)> Calls { get; } = [];

    public Task<IconImage?> FetchAsync(Store store, string appId, IReadOnlyList<string> countries, CancellationToken ct)
    {
        Calls.Add((store, appId, countries));
        return Task.FromResult(Known.Contains(appId) ? new IconImage(Png, "image/png") : null);
    }
}
