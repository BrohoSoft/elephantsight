using System.Net;
using System.Net.Http.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Sync;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Flarelytics.Tests.Integration;

/// <summary>Le icone: il worker le scarica, l'API le serve con un indirizzo che non si indovina.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class IconTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AppleId = "1234567890";
    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(connection);
        _ = _app.Services;
        _sync = new SyncHost(_app, connection, backfillDays: 1);

        // Cambi presenti: il giro non va alla BCE.
        await using var scope = _sync.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        db.Add(new ExchangeRate { Date = DateOnly.FromDateTime(DateTime.UtcNow), Currency = "USD", UnitsPerEuro = 1.1m });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _app.DisposeAsync();
    }

    private async Task<(Account Account, Guid Project)> ProjectWithAppAsync(string appId)
    {
        var account = await _app.SignUpAsync();
        var credential = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/credentials/app-store", TestKeys.AppleRequest()))
            .ReadJsonAsync()).GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name = "Meteo" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/apps", new { credentialId = credential, externalAppId = appId });
        return (account, project);
    }

    private Task RunAsync() => _sync.Services.GetRequiredService<SyncCoordinator>().RunOnceAsync(CancellationToken.None);

    [Fact]
    public async Task L_icona_scaricata_dal_worker_compare_nel_progetto_e_si_apre_senza_login()
    {
        var (account, project) = await ProjectWithAppAsync(AppleId);
        _sync.Icons.Known.Add(AppleId);

        await RunAsync();

        var body = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}")).ReadJsonAsync();
        var url = body.GetProperty("iconUrl").GetString();
        Assert.NotNull(url);
        Assert.Equal(url, body.GetProperty("apps")[0].GetProperty("iconUrl").GetString());
        Assert.DoesNotContain(AppleId, url); // l'indirizzo non rivela quale app è

        // Senza token: è un <img>.
        var image = await _app.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeAppIconSource.Png, await image.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Un_app_non_pubblicata_non_ha_icona_e_non_si_richiede_a_ogni_giro()
    {
        var (account, project) = await ProjectWithAppAsync("9999999999");

        await RunAsync();
        await RunAsync();

        var body = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}")).ReadJsonAsync();
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("iconUrl").ValueKind);
        Assert.Single(_sync.Icons.Calls); // il secondo giro aspetta il giorno dopo
    }

    [Fact]
    public async Task Un_indirizzo_inventato_non_trova_niente()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _app.CreateClient().GetAsync($"/api/v1/icons/{Guid.NewGuid()}")).StatusCode);
    }
}
