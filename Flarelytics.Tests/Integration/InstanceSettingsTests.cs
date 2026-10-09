using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Instance;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flarelytics.Tests.Integration;

/// <summary>Le impostazioni dell'istanza dal pannello: solo per i suoi amministratori, cifrate, valide subito, con il .env come riserva.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class InstanceSettingsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());
        _ = _app.Services;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Un utente amministratore dell'istanza (come chi ha fatto l'installazione).</summary>
    private async Task<Account> AdminAsync()
    {
        var a = await _app.SignUpAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        (await db.Set<User>().SingleAsync(u => u.Email == a.Email)).SetInstanceAdmin(true);
        await db.SaveChangesAsync();
        return a;
    }

    private static JsonElement Field(JsonElement groups, string group, string name) =>
        groups.EnumerateArray().Single(g => g.GetProperty("group").GetString() == group)
            .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);

    private static async Task<string> MetaClientIdAsync(Account a)
    {
        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/meta/start", null)).ReadJsonAsync();
        return QueryHelpers.ParseQuery(new Uri(start.GetProperty("url").GetString()!).Query)["client_id"].ToString();
    }

    [Fact]
    public async Task Le_chiavi_messe_dal_pannello_valgono_subito_non_si_rileggono_e_tolte_torna_il_env()
    {
        var admin = await AdminAsync();
        var settings = await (await admin.Client.GetAsync("/api/v1/instance/settings")).ReadJsonAsync();
        Assert.Equal("app-meta", Field(settings, "meta", "appId").GetProperty("value").GetString());
        Assert.Equal("env", Field(settings, "meta", "appId").GetProperty("source").GetString());
        Assert.Equal("app-meta", await MetaClientIdAsync(admin));

        var saved = await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/meta", new { appId = "app-dal-pannello", appSecret = "segreto-dal-pannello" })).ReadJsonAsync();
        Assert.Equal("app-dal-pannello", Field(saved, "meta", "appId").GetProperty("value").GetString());
        Assert.Equal("panel", Field(saved, "meta", "appSecret").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, Field(saved, "meta", "appSecret").GetProperty("value").ValueKind);
        Assert.DoesNotContain("segreto-dal-pannello", saved.GetRawText());

        // Vale subito, senza riavvio.
        Assert.Equal("app-dal-pannello", await MetaClientIdAsync(admin));

        // A database è cifrato, anche l'App ID.
        using (var scope = _app.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<InstanceSetting>().ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.DoesNotContain("pannello", r.ProtectedValue));
        }

        // Cambiare solo l'App ID lascia il segreto com'era (non si rimanda quello che non si vede).
        await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/meta", new { appId = "app-due" });
        var kept = await (await admin.Client.GetAsync("/api/v1/instance/settings")).ReadJsonAsync();
        Assert.True(Field(kept, "meta", "appSecret").GetProperty("set").GetBoolean());

        // Tolti dal pannello, torna a valere il .env.
        var cleared = await (await admin.Client.DeleteAsync("/api/v1/instance/settings/meta")).ReadJsonAsync();
        Assert.Equal("env", Field(cleared, "meta", "appId").GetProperty("source").GetString());
        Assert.Equal("app-meta", await MetaClientIdAsync(admin));
    }

    [Fact]
    public async Task Chi_fa_l_installazione_amministra_l_istanza()
    {
        var client = _app.CreateClient();
        var setup = await client.PostAsJsonAsync("/api/v1/setup", new { email = "admin@example.com", password = TestApi.Password, fullName = "Admin", organizationName = "Studio" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await setup.ReadJsonAsync()).GetProperty("accessToken").GetString());
        Assert.True((await (await client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("isInstanceAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/instance/settings")).StatusCode);
    }

    [Fact]
    public async Task Solo_un_amministratore_dell_istanza_le_vede_anche_se_owner_di_un_organizzazione()
    {
        var owner = await _app.SignUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.GetAsync("/api/v1/instance/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.PutAsJsonAsync("/api/v1/instance/settings/smtp", new { host = "smtp.example.com" })).StatusCode);
        Assert.False((await (await owner.Client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("isInstanceAdmin").GetBoolean());

        var admin = await AdminAsync();
        Assert.True((await (await admin.Client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("isInstanceAdmin").GetBoolean());
        Assert.Equal("unknown_setting", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/smtp", new { segreto = "x" })).ProblemCodeAsync());
        Assert.Equal("smtp_port", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/smtp", new { port = "settanta" })).ProblemCodeAsync());
    }

    [Fact]
    public async Task Gli_amministratori_si_nominano_e_si_tolgono_ma_ne_resta_sempre_uno()
    {
        var admin = await AdminAsync();
        var other = await _app.SignUpAsync();

        var added = await (await admin.Client.PostAsJsonAsync("/api/v1/instance/admins", new { email = other.Email })).ReadJsonAsync();
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/v1/instance/settings")).StatusCode);

        var adminId = (await (await admin.Client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await other.Client.DeleteAsync($"/api/v1/instance/admins/{adminId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.GetAsync("/api/v1/instance/settings")).StatusCode);

        var otherId = added.GetProperty("userId").GetGuid();
        Assert.Equal("last_instance_admin", await (await other.Client.DeleteAsync($"/api/v1/instance/admins/{otherId}")).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync("/api/v1/instance/admins", new { email = "nessuno@example.com" })).StatusCode);
    }
}
