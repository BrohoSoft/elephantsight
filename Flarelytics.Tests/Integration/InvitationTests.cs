using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>Inviti, ruoli e rimozione dei membri.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class InvitationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<string> InviteAsync(Account by, string email, string role)
    {
        var response = await by.Client.PostAsJsonAsync($"/api/v1/orgs/{by.OrgId}/invitations", new { email, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Il link torna anche nella risposta (serve quando l'email non è
        // configurata) ed è lo stesso dell'email.
        var token = _app.LinkToken(email);
        Assert.EndsWith(Uri.EscapeDataString(token), (await response.ReadJsonAsync()).GetProperty("link").GetString());
        return token;
    }

    [Fact]
    public async Task Chi_si_registra_da_un_invito_entra_subito_nell_organizzazione()
    {
        var owner = await _app.SignUpAsync();
        var email = $"u{Guid.NewGuid():N}@example.com";
        var token = await InviteAsync(owner, email, "Admin");

        var preview = await (await _app.CreateClient().GetAsync($"/api/v1/invitations/preview?token={Uri.EscapeDataString(token)}")).ReadJsonAsync();
        Assert.False(preview.GetProperty("accountExists").GetBoolean());

        var client = _app.CreateClient();
        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = TestApi.Password, fullName = "Luigi Verdi", invitationToken = token
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // Nessuna conferma email: il link d'invito l'ha già provata.
        await client.LoginAsync(email);
        var orgs = (await (await client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("organizations");
        Assert.Equal(1, orgs.GetArrayLength());
        Assert.Equal(owner.OrgId, orgs[0].GetProperty("id").GetGuid());
        Assert.Equal("Admin", orgs[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Un_utente_esistente_accetta_e_vede_i_progetti_dell_organizzazione()
    {
        var owner = await _app.SignUpAsync();
        var guest = await _app.SignUpAsync();
        await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/projects", new { name = "Condiviso" });

        var token = await InviteAsync(owner, guest.Email, "Viewer");
        var accepted = await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var projects = await (await guest.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/projects")).ReadJsonAsync();
        Assert.Equal("Condiviso", projects[0].GetProperty("name").GetString());

        // Viewer: legge ma non modifica.
        var create = await guest.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/projects", new { name = "No" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task L_invito_vale_solo_per_l_indirizzo_a_cui_e_stato_mandato()
    {
        var owner = await _app.SignUpAsync();
        var other = await _app.SignUpAsync();
        var token = await InviteAsync(owner, $"u{Guid.NewGuid():N}@example.com", "Viewer");

        var accepted = await other.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token });

        Assert.Equal(HttpStatusCode.Forbidden, accepted.StatusCode);
        Assert.Equal("invitation_email_mismatch", await accepted.ProblemCodeAsync());
    }

    [Fact]
    public async Task Un_invito_revocato_o_sostituito_non_vale_piu()
    {
        var owner = await _app.SignUpAsync();
        var guest = await _app.SignUpAsync();

        var first = await InviteAsync(owner, guest.Email, "Viewer");
        var second = await InviteAsync(owner, guest.Email, "Admin");

        var stale = await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = first });
        Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);

        var pending = await (await owner.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/invitations")).ReadJsonAsync();
        var id = pending[0].GetProperty("id").GetGuid();
        await owner.Client.DeleteAsync($"/api/v1/orgs/{owner.OrgId}/invitations/{id}");

        Assert.Equal(HttpStatusCode.Gone, (await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = second })).StatusCode);
    }

    [Fact]
    public async Task Un_admin_non_puo_invitare_un_owner()
    {
        var owner = await _app.SignUpAsync();
        var admin = await _app.SignUpAsync();
        await admin.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = await InviteAsync(owner, admin.Email, "Admin") });

        var response = await admin.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/invitations",
            new { email = "capo@example.com", role = "Owner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task L_ultimo_owner_non_esce_e_non_si_declassa()
    {
        var owner = await _app.SignUpAsync();
        var me = (await (await owner.Client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("id").GetGuid();

        var leave = await owner.Client.DeleteAsync($"/api/v1/orgs/{owner.OrgId}/members/{me}");
        Assert.Equal("last_owner", await leave.ProblemCodeAsync());

        var demote = await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/members/{me}", new { role = "Admin" });
        Assert.Equal("last_owner", await demote.ProblemCodeAsync());
    }

    [Fact]
    public async Task Un_membro_rimosso_perde_l_accesso_subito()
    {
        var owner = await _app.SignUpAsync();
        var guest = await _app.SignUpAsync();
        await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = await InviteAsync(owner, guest.Email, "Admin") });
        Assert.Equal(HttpStatusCode.OK, (await guest.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/projects")).StatusCode);

        var members = await (await owner.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/members")).ReadJsonAsync();
        var guestId = members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == guest.Email).GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/orgs/{owner.OrgId}/members/{guestId}")).StatusCode);

        // Lo stesso access token di prima: il permesso si controlla a ogni richiesta, non alla scadenza.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.Client.GetAsync($"/api/v1/orgs/{owner.OrgId}/projects")).StatusCode);
    }
}
