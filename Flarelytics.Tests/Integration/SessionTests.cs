using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>Registrazione, conferma, accesso e ciclo di vita della sessione.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SessionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task La_registrazione_crea_utente_organizzazione_e_abbonamento_attivo()
    {
        var account = await _app.SignUpAsync();

        var me = await (await account.Client.GetAsync("/api/v1/me")).ReadJsonAsync();
        var org = me.GetProperty("organizations")[0];
        Assert.Equal(account.OrgId, org.GetProperty("id").GetGuid());
        Assert.Equal("Owner", org.GetProperty("role").GetString());

        // I pagamenti sono in sordina: il piano risulta pagato subito.
        var subscription = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/subscription")).ReadJsonAsync();
        Assert.True(subscription.GetProperty("isActive").GetBoolean());
        Assert.Equal("starter", subscription.GetProperty("plan").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Senza_conferma_dell_email_non_si_entra()
    {
        var client = _app.CreateClient();
        var email = $"u{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = TestApi.Password, fullName = "Mario Rossi", organizationName = "Acme"
        });

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password });

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("email_not_confirmed", await login.ProblemCodeAsync());
    }

    [Fact]
    public async Task Password_sbagliata_ed_email_inesistente_rispondono_allo_stesso_modo()
    {
        var account = await _app.SignUpAsync();
        var client = _app.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = account.Email, password = "sbagliata-123" });
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nessuno@example.com", password = "sbagliata-123" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrongPassword.ProblemCodeAsync(), await unknown.ProblemCodeAsync());
    }

    [Fact]
    public async Task Il_refresh_ruota_il_cookie_e_il_riuso_chiude_tutte_le_sessioni()
    {
        var account = await _app.SignUpAsync();

        // Il cookie che il login ha lasciato nel client.
        var first = await account.Client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stolen = first.RequestMessage!.Headers.GetValues("Cookie").Single();

        var second = await account.Client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Qualcuno ripresenta il cookie già speso.
        var thief = _app.CreateClient(new() { HandleCookies = false });
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replay.Headers.Add("Cookie", stolen);
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.SendAsync(replay)).StatusCode);

        // Anche la sessione legittima è stata chiusa: non si sa quale delle due fosse il ladro.
        Assert.Equal(HttpStatusCode.Unauthorized, (await account.Client.PostAsync("/api/v1/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Il_cambio_password_invalida_gli_access_token_gia_emessi()
    {
        var account = await _app.SignUpAsync();
        Assert.Equal(HttpStatusCode.OK, (await account.Client.GetAsync("/api/v1/me")).StatusCode);

        var anonymous = _app.CreateClient();
        await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = account.Email });
        var token = Uri.UnescapeDataString(System.Text.RegularExpressions.Regex
            .Match(_app.Emails.LastTo(account.Email).Body, @"token=(\S+)").Groups[1].Value);

        var reset = await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "una-password-nuova" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await account.Client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task La_stessa_email_non_si_registra_due_volte_anche_cambiando_le_maiuscole()
    {
        var account = await _app.SignUpAsync();

        var again = await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = account.Email.ToUpperInvariant(), password = TestApi.Password, fullName = "Altro", organizationName = "Altra"
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("email_taken", await again.ProblemCodeAsync());
    }
}
