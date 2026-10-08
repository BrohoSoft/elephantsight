using System.Net.Http.Json;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Sync;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Flarelytics.Tests.Integration.Infrastructure.GoogleReports;

namespace Flarelytics.Tests.Integration;

/// <summary>Dal bucket di Play Console alla dashboard.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class GoogleSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Package = "com.esempio.meteo_pro";
    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;
    private DateOnly _thisMonth;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(connection);
        _ = _app.Services;
        _sync = new SyncHost(_app, connection, backfillDays: 60);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _thisMonth = new DateOnly(today.Year, today.Month, 1);

        // Cambi già presenti: il giro non deve andare alla BCE.
        await using var scope = _sync.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Flarelytics.Core.Database.FlarelyticsDbContext>();
        // Due mesi di cambi: i report finanziari del mese scorso li cercano dal primo del mese.
        db.AddRange(Enumerable.Range(0, 70).Select(i => new Flarelytics.Core.Database.Entities.ExchangeRate { Date = today.AddDays(-i), Currency = "USD", UnitsPerEuro = 1.1m }));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _app.DisposeAsync();
    }

    private async Task<Account> SetupAsync()
    {
        var account = await _app.SignUpAsync();
        var credential = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/credentials/google-play", new
        {
            label = "Play", serviceAccountJson = TestKeys.GoogleServiceAccountJson(), reportsBucket = "gs://pubsite_prod_rev_0123456789/"
        })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name = "Meteo" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/apps", new { credentialId = credential, externalAppId = Package });
        return account;
    }

    /// <summary>"Sincronizza ora" sull'unica chiave dell'account: senza, il secondo giro aspetterebbe sei ore.</summary>
    private static async Task RequestSyncAsync(Account account)
    {
        var id = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/credentials/{id}/sync", null);
    }

    private Task RunAsync() => _sync.Services.GetRequiredService<SyncCoordinator>().RunOnceAsync(CancellationToken.None);

    private async Task<System.Text.Json.JsonElement> GoogleTotalsAsync(Account account) =>
        (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics?days=365")).ReadJsonAsync())
            .GetProperty("byStore").EnumerateArray().Single(s => s.GetProperty("store").GetString() == "GooglePlay");

    [Fact]
    public async Task Le_installazioni_di_google_arrivano_nella_dashboard()
    {
        var account = await SetupAsync();
        var lastMonth = _thisMonth.AddMonths(-1);
        _sync.Google.Files[Name(Package, lastMonth)] = Csv(Package,
            new Row(lastMonth.AddDays(4), "IT", 10, 30, 1),
            new Row(lastMonth.AddDays(5), "US", 5, 2, 0));
        _sync.Google.Files[Name(Package, _thisMonth)] = Csv(Package, new Row(_thisMonth, "IT", 3, 1, 2));
        // Un file di un'altra dimensione, che va ignorato.
        _sync.Google.Files[$"stats/installs/installs_{Package}_{_thisMonth:yyyyMM}_device.csv"] = Csv(Package, new Row(_thisMonth, "IT", 999, 0, 0));

        await RunAsync();

        var google = await GoogleTotalsAsync(account);
        Assert.Equal(18, google.GetProperty("downloads").GetInt32());
        Assert.Equal(33, google.GetProperty("updates").GetInt32());
        Assert.Equal(3, google.GetProperty("uninstalls").GetInt32());
        Assert.Equal(2, _sync.Google.Downloaded.Count);

        // I report finanziari non si leggono, e la dashboard non promette ricavi.
        var metrics = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics?days=365")).ReadJsonAsync();
        var coverage = metrics.GetProperty("coverage").EnumerateArray().Single(c => c.GetProperty("store").GetString() == "GooglePlay");
        Assert.DoesNotContain("proceeds", coverage.GetProperty("metrics").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Un_file_invariato_non_si_riscarica_uno_aggiornato_sostituisce_i_numeri()
    {
        var account = await SetupAsync();
        _sync.Google.Files[Name(Package, _thisMonth)] = Csv(Package, new Row(_thisMonth, "IT", 3, 0, 0));
        await RunAsync();

        // Stesso file: il secondo giro non lo scarica.
        await RequestSyncAsync(account);
        await RunAsync();
        Assert.Single(_sync.Google.Downloaded);

        // Google riscrive il mese con un giorno in più e un valore corretto.
        _sync.Google.Files[Name(Package, _thisMonth)] = Csv(Package,
            new Row(_thisMonth, "IT", 4, 0, 0),
            new Row(_thisMonth.AddDays(1), "IT", 6, 0, 0));
        await RequestSyncAsync(account);
        await RunAsync();

        Assert.Equal(2, _sync.Google.Downloaded.Count);
        Assert.Equal(10, (await GoogleTotalsAsync(account)).GetProperty("downloads").GetInt32()); // 4 + 6, non 3 + 4 + 6
    }

    [Fact]
    public async Task Senza_accesso_al_bucket_la_chiave_resta_parziale_con_il_motivo()
    {
        var account = await SetupAsync();
        _sync.Google.Fail = new GoogleAccessException(GoogleAccessProblem.NoBucketAccess, "Il service account non può leggere il bucket dei report.");

        await RunAsync();

        var credential = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0];
        Assert.Equal("Limited", credential.GetProperty("status").GetString());
        Assert.Contains("bucket", credential.GetProperty("lastSyncError").GetString());

        // Quando l'invito diventa attivo, la chiave torna valida da sola.
        _sync.Google.Fail = null;
        _sync.Google.Files[Name(Package, _thisMonth)] = Csv(Package, new Row(_thisMonth, "IT", 1, 0, 0));
        await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/credentials/{credential.GetProperty("id").GetGuid()}/sync", null);
        await RunAsync();

        credential = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0];
        Assert.Equal("Valid", credential.GetProperty("status").GetString());
    }
}
