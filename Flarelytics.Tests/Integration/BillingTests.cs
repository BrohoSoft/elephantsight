using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>Intestatario delle fatture, annullamento e riattivazione dell'abbonamento.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class BillingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object ItalianCompany(string? sdi = "M5UXCR1", string? pec = null) => new
    {
        companyName = "Esempio S.r.l.", vatNumber = "IT01234567890", taxCode = (string?)null,
        addressLine = "Via Roma 1", city = "Trento", postalCode = "38122", province = "tn", countryCode = "it",
        billingEmail = "Amministrazione@esempio.it", sdiCode = sdi, pec
    };

    [Fact]
    public async Task L_intestatario_si_salva_e_si_rilegge_normalizzato()
    {
        var owner = await _app.SignUpAsync();

        var saved = await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/billing/profile", ItalianCompany());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var overview = await (await owner.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/billing")).ReadJsonAsync();
        var profile = overview.GetProperty("profile");
        Assert.Equal("IT", profile.GetProperty("countryCode").GetString());
        Assert.Equal("TN", profile.GetProperty("province").GetString());
        Assert.Equal("amministrazione@esempio.it", profile.GetProperty("billingEmail").GetString());
        Assert.Equal(1, overview.GetProperty("memberCount").GetInt32());
        Assert.Equal("manual", overview.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Un_azienda_italiana_senza_sdi_ne_pec_si_rifiuta()
    {
        var owner = await _app.SignUpAsync();

        var response = await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/billing/profile", ItalianCompany(sdi: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("SdiCode", out _));
    }

    [Fact]
    public async Task L_intestatario_di_un_altra_organizzazione_non_si_vede()
    {
        var alice = await _app.SignUpAsync();
        var bob = await _app.SignUpAsync();
        await alice.Client.PutAsJsonAsync($"/api/v1/orgs/{alice.OrgId}/billing/profile", ItalianCompany());

        var overview = await (await bob.Client.GetAsync($"/api/v1/orgs/{bob.OrgId}/billing")).ReadJsonAsync();

        Assert.Equal(System.Text.Json.JsonValueKind.Null, overview.GetProperty("profile").ValueKind);
    }

    [Fact]
    public async Task Annullato_blocca_le_modifiche_e_scegliere_un_piano_lo_riattiva()
    {
        var owner = await _app.SignUpAsync();
        var projects = $"/api/v1/orgs/{owner.OrgId}/projects";

        var cancel = await owner.Client.PostAsync($"/api/v1/orgs/{owner.OrgId}/subscription/cancel", null);
        Assert.False((await cancel.ReadJsonAsync()).GetProperty("isActive").GetBoolean());

        var blocked = await owner.Client.PostAsJsonAsync(projects, new { name = "Dopo" });
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);

        // La lettura resta: i dati non spariscono con l'abbonamento.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(projects)).StatusCode);

        await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/subscription", new { plan = "pro" });
        Assert.Equal(HttpStatusCode.Created, (await owner.Client.PostAsJsonAsync(projects, new { name = "Dopo" })).StatusCode);
    }

    [Fact]
    public async Task Solo_l_owner_tocca_la_fatturazione()
    {
        var owner = await _app.SignUpAsync();
        var admin = await _app.SignUpAsync();
        await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/invitations", new { email = admin.Email, role = "Admin" });
        await admin.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = _app.LinkToken(admin.Email) });

        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/billing/profile", ItalianCompany())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.Client.PostAsync($"/api/v1/orgs/{owner.OrgId}/subscription/cancel", null)).StatusCode);
    }
}
