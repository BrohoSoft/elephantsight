using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>I limiti del piano e il cambio di piano.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class PlanTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Oltre_il_limite_del_piano_non_si_creano_progetti_finche_non_si_sale()
    {
        var account = await _app.SignUpAsync();
        var projects = $"/api/v1/orgs/{account.OrgId}/projects";

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await account.Client.PostAsJsonAsync(projects, new { name = $"P{i}" })).StatusCode);
        }

        var fourth = await account.Client.PostAsJsonAsync(projects, new { name = "P4" });
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("plan_limit_reached", await fourth.ProblemCodeAsync());

        var upgrade = await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/subscription", new { plan = "pro" });
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await account.Client.PostAsJsonAsync(projects, new { name = "P4" })).StatusCode);

        // E adesso non si torna allo starter con quattro progetti.
        var downgrade = await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/subscription", new { plan = "starter" });
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);
        Assert.Equal("plan_too_small", await downgrade.ProblemCodeAsync());
    }

    [Fact]
    public async Task Due_progetti_con_lo_stesso_nome_no()
    {
        var account = await _app.SignUpAsync();
        var projects = $"/api/v1/orgs/{account.OrgId}/projects";

        await account.Client.PostAsJsonAsync(projects, new { name = "Acme" });
        var again = await account.Client.PostAsJsonAsync(projects, new { name = "Acme" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
