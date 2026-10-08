using System.Net;
using System.Net.Http.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Sync;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Flarelytics.Tests.Integration.Infrastructure.AppleReports;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// Dal report di Apple alla dashboard: il worker scarica e salva, l'API legge
/// le metriche.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AppId = "1234567890";
    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;
    private DateOnly _today;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(connection);
        _ = _app.Services; // avvia l'API: applica le migration
        _sync = new SyncHost(_app, connection);
        _today = AppleCalendar.Today(DateTime.UtcNow);

        // I cambi già aggiornati a ieri: così il worker non va a chiederli
        // alla BCE, e i test non dipendono dalla rete.
        await using var scope = _sync.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.AddRange(Enumerable.Range(0, 30).Select(i => new ExchangeRate { Date = today.AddDays(-i), Currency = "USD", UnitsPerEuro = 1.25m }));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Un'organizzazione con una chiave Apple e un progetto con l'app collegata.</summary>
    private async Task<(Account Account, Guid Credential, Guid Project)> SetupAsync()
    {
        var account = await _app.SignUpAsync();
        var credential = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/credentials/app-store", TestKeys.AppleRequest()))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name = "Meteo" }))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/apps",
            new { credentialId = credential, externalAppId = AppId, displayName = "Meteo" });

        _sync.Apple.Skus.Add(("METEO", AppId));
        return (account, credential, project);
    }

    private Task RunAsync() => _sync.Services.GetRequiredService<SyncCoordinator>().RunOnceAsync(CancellationToken.None);

    private void Report(int daysAgo, params Row[] rows)
    {
        var date = _today.AddDays(-daysAgo);
        _sync.Apple.Reports[date] = Gzip(date, rows);
    }

    [Fact]
    public async Task Il_report_di_apple_arriva_nella_dashboard_del_progetto()
    {
        var (account, _, project) = await SetupAsync();
        Report(2,
            new Row("METEO", "1F", 10, 0, "EUR", "IT", "EUR", AppId, 0),
            new Row("METEO", "1", 2, 1.40m, "EUR", "DE", "EUR", AppId, 1.99m));
        Report(3, new Row("PRO", "IA1", 4, 2.50m, "USD", "US", "USD", "5555", 2.99m, Parent: "METEO"));

        await RunAsync();

        var metrics = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics?days=7&projectId={project}")).ReadJsonAsync();
        var apple = metrics.GetProperty("byStore")[0];

        Assert.Equal("AppStore", apple.GetProperty("store").GetString());
        Assert.Equal(12, apple.GetProperty("downloads").GetInt32());
        Assert.Equal(4, apple.GetProperty("inAppPurchases").GetInt32());
        Assert.Equal(2.80m + 8.00m, apple.GetProperty("proceedsEur").GetDecimal());  // 2 × 1,40 + 4 × 2,50 / 1,25
        Assert.Equal(_today.AddDays(-2), DateOnly.Parse(metrics.GetProperty("to").GetString()!));
        Assert.Equal("IT", metrics.GetProperty("countries")[0].GetProperty("countryCode").GetString());
    }

    [Fact]
    public async Task Il_secondo_giro_non_riscarica_e_non_duplica()
    {
        var (account, credential, _) = await SetupAsync();
        Report(5, new Row("METEO", "1F", 7, 0, "EUR", "IT", "EUR", AppId, 0));

        await RunAsync();
        var firstRequests = _sync.Apple.Requested.Count;
        Assert.Equal(10, firstRequests); // tutta la finestra di storico

        // Una sincronizzazione chiesta a mano: il worker la riprende subito.
        await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/credentials/{credential}/sync", null);
        await RunAsync();

        // Solo i giorni recenti trovati vuoti si richiedono di nuovo.
        var again = _sync.Apple.Requested.Skip(firstRequests).ToList();
        Assert.All(again, d => Assert.True(d >= _today.AddDays(-3)));
        Assert.DoesNotContain(_today.AddDays(-5), again);

        var metrics = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics?days=30")).ReadJsonAsync();
        Assert.Equal(7, metrics.GetProperty("byStore")[0].GetProperty("downloads").GetInt32());
    }

    [Fact]
    public async Task L_avanzamento_si_vede_sulla_credenziale()
    {
        var (account, _, _) = await SetupAsync();
        Report(1, new Row("METEO", "1F", 1, 0, "EUR", "IT", "EUR", AppId, 0));

        await RunAsync();

        var credential = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0];
        Assert.Equal(10, credential.GetProperty("daysImported").GetInt32());
        Assert.False(credential.GetProperty("syncRequested").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, credential.GetProperty("lastSyncCompletedAtUtc").ValueKind);
        Assert.Equal(_today.AddDays(-1), DateOnly.Parse(credential.GetProperty("latestReportDate").GetString()!));

        // Il file grezzo è sul disco.
        Assert.Single(Directory.GetFiles(_sync.ReportsDirectory, "*.gz", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Una_chiave_revocata_ferma_la_sincronizzazione_e_si_segna_non_valida()
    {
        var (account, _, _) = await SetupAsync();
        _sync.Apple.ForceOutcome = AppleFetchOutcome.Unauthorized;

        await RunAsync();

        Assert.Single(_sync.Apple.Requested);
        var credential = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0];
        Assert.Equal("Invalid", credential.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(credential.GetProperty("lastSyncError").GetString()));
    }

    [Fact]
    public async Task Un_ruolo_insufficiente_lascia_la_chiave_valida_ma_parziale()
    {
        var (account, _, _) = await SetupAsync();
        _sync.Apple.ForceOutcome = AppleFetchOutcome.Forbidden;

        await RunAsync();

        Assert.Single(_sync.Apple.Requested);
        var credential = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/credentials")).ReadJsonAsync())[0];
        Assert.Equal("Limited", credential.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Le_metriche_di_un_tenant_non_si_vedono_da_un_altro()
    {
        var (alice, _, _) = await SetupAsync();
        Report(2, new Row("METEO", "1F", 10, 0, "EUR", "IT", "EUR", AppId, 0));
        await RunAsync();

        // Bob collega la stessa app (stesso Apple ID) ma con una sua chiave:
        // non deve vedere i numeri scaricati per Alice.
        var bob = await _app.SignUpAsync();
        var bobCredential = (await (await bob.Client.PostAsJsonAsync($"/api/v1/orgs/{bob.OrgId}/credentials/app-store", TestKeys.AppleRequest()))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        var bobProject = (await (await bob.Client.PostAsJsonAsync($"/api/v1/orgs/{bob.OrgId}/projects", new { name = "Copia" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        await bob.Client.PutAsJsonAsync($"/api/v1/orgs/{bob.OrgId}/projects/{bobProject}/apps", new { credentialId = bobCredential, externalAppId = AppId });

        var metrics = await (await bob.Client.GetAsync($"/api/v1/orgs/{bob.OrgId}/metrics")).ReadJsonAsync();
        Assert.Equal(0, metrics.GetProperty("byStore").GetArrayLength());

        var aliceMetrics = await (await alice.Client.GetAsync($"/api/v1/orgs/{alice.OrgId}/metrics")).ReadJsonAsync();
        Assert.Equal(10, aliceMetrics.GetProperty("byStore")[0].GetProperty("downloads").GetInt32());
    }

    [Fact]
    public async Task Un_parser_nuovo_rielabora_i_report_senza_riscaricarli()
    {
        var (account, _, _) = await SetupAsync();
        Report(2, new Row("METEO", "1F", 10, 0, "EUR", "IT", "EUR", AppId, 0));
        await RunAsync();

        // Si simula un report elaborato da una versione vecchia del parser.
        await using (var scope = _sync.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(account.OrgId);
            await db.Set<ReportFile>().Where(f => f.Status == ReportFileStatus.Stored)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.ParserVersion, 0));
            await db.Set<DailyAppMetric>().ExecuteDeleteAsync();

            var credential = await db.Set<StoreCredential>().SingleAsync();
            var processed = await scope.ServiceProvider.GetRequiredService<ReportProcessor>().ProcessPendingAsync(credential, default);
            Assert.Equal(1, processed);
        }

        var metrics = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics")).ReadJsonAsync();
        Assert.Equal(10, metrics.GetProperty("byStore")[0].GetProperty("downloads").GetInt32());
    }

    [Fact]
    public async Task Senza_app_collegate_la_dashboard_lo_dice()
    {
        var account = await _app.SignUpAsync();

        var metrics = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics")).ReadJsonAsync();

        Assert.False(metrics.GetProperty("hasLinkedApps").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/metrics?projectId={Guid.NewGuid()}")).StatusCode);
    }
}
