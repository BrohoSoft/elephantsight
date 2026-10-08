using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>L'installer: il primo utente, e nessuna registrazione libera.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SetupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object Admin(string email = "admin@example.com") =>
        new { email, password = TestApi.Password, fullName = "Mario Rossi", organizationName = "Esempio" };

    [Fact]
    public async Task Un_istanza_nuova_chiede_l_installazione_e_l_installer_apre_la_sessione()
    {
        var client = _app.CreateClient();
        Assert.True((await (await client.GetAsync("/api/v1/instance")).ReadJsonAsync()).GetProperty("setupRequired").GetBoolean());

        var setup = await client.PostAsJsonAsync("/api/v1/setup", Admin());
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // Già dentro: il token è nella risposta, il refresh nel cookie.
        var token = (await setup.ReadJsonAsync()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var me = await (await client.GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.Equal("Owner", me.GetProperty("organizations")[0].GetProperty("role").GetString());

        Assert.False((await (await client.GetAsync("/api/v1/instance")).ReadJsonAsync()).GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task Una_volta_installato_l_installer_non_si_riapre()
    {
        await _app.CreateClient().PostAsJsonAsync("/api/v1/setup", Admin());

        var again = await _app.CreateClient().PostAsJsonAsync("/api/v1/setup", Admin("intruso@example.com"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("setup_done", await again.ProblemCodeAsync());
    }

    [Fact]
    public async Task Due_installer_nello_stesso_istante_creano_un_solo_amministratore()
    {
        var attempts = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            _app.CreateClient().PostAsJsonAsync("/api/v1/setup", Admin($"admin{i}@example.com"))));

        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(attempts.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Senza_invito_non_ci_si_registra()
    {
        await _app.CreateClient().PostAsJsonAsync("/api/v1/setup", Admin());

        var register = await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "libero@example.com", password = TestApi.Password, fullName = "Libero", invitationToken = "inventato"
        });

        Assert.Equal(HttpStatusCode.Gone, register.StatusCode);
        Assert.Equal("invitation_invalid", await register.ProblemCodeAsync());
    }
}
