using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// Progetti e sezioni per membro: chi vede solo alcuni progetti, o solo il
/// Social, non vede né tocca il resto; gli account social si condividono fra
/// progetti e i post restano del loro progetto.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class AccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Mastodon = "https://mastodon.example";

    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());
        _ = _app.Services;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<Guid> CreateProjectAsync(Account owner, string name) =>
        (await (await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/projects", new { name })).ReadJsonAsync()).GetProperty("id").GetGuid();

    private async Task<Guid> ConnectMastodonAsync(Account owner)
    {
        _app.SocialApis.On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/verify_credentials$", """{"id":"42","username":"meteo","display_name":"Meteo"}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v2/instance$", """{"configuration":{"statuses":{"max_characters":500}}}""");
        var response = await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/accounts/mastodon",
            new { instanceUrl = "mastodon.example", accessToken = "token" });
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Invita e fa accettare: ne esce un membro dell'organizzazione di <paramref name="owner"/> con l'accesso indicato.</summary>
    private async Task<Account> InviteAsync(Account owner, string role, object? access)
    {
        var guest = await _app.SignUpAsync();
        var invite = await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/invitations", new { email = guest.Email, role, access });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var accepted = await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = _app.LinkToken(guest.Email) });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return guest with { OrgId = owner.OrgId };
    }

    private static object Post(string text, Guid? projectId, params Guid[] accounts) => new
    {
        text, scheduledAtUtc = DateTime.UtcNow.AddDays(1), isDraft = false, projectId, accountIds = accounts, media = Array.Empty<object>()
    };

    private static async Task<List<JsonElement>> CalendarAsync(Account a, Guid? projectId = null)
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(5).ToString("O"));
        var response = await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts?from={from}&to={to}{(projectId is { } p ? $"&projectId={p}" : "")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).EnumerateArray().ToList();
    }

    [Fact]
    public async Task Un_account_si_condivide_fra_progetti_e_i_post_restano_del_loro_progetto()
    {
        var owner = await _app.SignUpAsync();
        var app = await CreateProjectAsync(owner, "App meteo");
        var personal = await CreateProjectAsync(owner, "Personale");
        var account = await ConnectMastodonAsync(owner);

        // Senza progetti l'account non si usa nei post di un progetto (nemmeno in bozza).
        var unlinked = await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("App", app, account));
        Assert.Equal("account_not_in_project", await unlinked.ProblemCodeAsync());

        var linked = await (await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/accounts/{account}/projects",
            new { projectIds = new[] { app, personal } })).ReadJsonAsync();
        Assert.Equal(2, linked.GetProperty("projectIds").GetArrayLength());

        Assert.Equal(HttpStatusCode.Created, (await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("Nuova versione", app, account))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("Il mio talk", personal, account))).StatusCode);

        // Fuori, il calendario completo; dentro un progetto, solo il suo.
        Assert.Equal(2, (await CalendarAsync(owner)).Count);
        Assert.Equal(["Nuova versione"], (await CalendarAsync(owner, app)).Select(p => p.GetProperty("text").GetString()).ToArray());
        Assert.Equal(["Il mio talk"], (await CalendarAsync(owner, personal)).Select(p => p.GetProperty("text").GetString()).ToArray());
    }

    [Fact]
    public async Task Chi_vede_un_progetto_solo_vede_e_scrive_solo_li()
    {
        var owner = await _app.SignUpAsync();
        var mine = await CreateProjectAsync(owner, "Cliente A");
        var other = await CreateProjectAsync(owner, "Cliente B");
        var shared = await ConnectMastodonAsync(owner);
        await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/accounts/{shared}/projects", new { projectIds = new[] { mine, other } });
        var otherPost = (await (await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("Di B", other, shared))).ReadJsonAsync()).GetProperty("id").GetGuid();
        await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("Di A", mine, shared));
        await owner.Client.PostAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/posts", Post("Dell'organizzazione", null, shared));

        var client = await InviteAsync(owner, "Admin", new { allProjects = false, projectIds = new[] { mine }, sections = "Store, Social" });
        var api = $"/api/v1/orgs/{owner.OrgId}";

        // /me dice cosa vede, per costruire il menu.
        var me = await (await client.Client.GetAsync("/api/v1/me")).ReadJsonAsync();
        var access = me.GetProperty("organizations").EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == owner.OrgId).GetProperty("access");
        Assert.False(access.GetProperty("allProjects").GetBoolean());
        Assert.Equal(mine, access.GetProperty("projectIds")[0].GetGuid());

        var projects = (await (await client.Client.GetAsync($"{api}/projects")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["Cliente A"], projects.Select(p => p.GetProperty("name").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.GetAsync($"{api}/projects/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.GetAsync($"{api}/projects/{other}/releases")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.GetAsync($"{api}/metrics?projectId={other}")).StatusCode);

        // Social: solo i post del suo progetto, anche cercandoli per id.
        Assert.Equal(["Di A"], (await CalendarAsync(client)).Select(p => p.GetProperty("text").GetString()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.GetAsync($"{api}/social/posts/{otherPost}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.DeleteAsync($"{api}/social/posts/{otherPost}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.PostAsJsonAsync($"{api}/social/posts", Post("In B", other, shared))).StatusCode);
        Assert.Equal("project_required", await (await client.Client.PostAsJsonAsync($"{api}/social/posts", Post("Senza progetto", null, shared))).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, (await client.Client.PostAsJsonAsync($"{api}/social/posts", Post("Altro in A", mine, shared))).StatusCode);

        // L'account condiviso si vede, ma solo con i progetti che il membro vede.
        var accounts = (await (await client.Client.GetAsync($"{api}/social/accounts")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal([mine], Assert.Single(accounts).GetProperty("projectIds").EnumerateArray().Select(p => p.GetGuid()).ToArray());

        // La gestione dell'organizzazione è di chi la vede tutta.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.PostAsJsonAsync($"{api}/projects", new { name = "Nuovo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.PutAsJsonAsync($"{api}/social/accounts/{shared}/projects", new { projectIds = new[] { mine } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.PostAsJsonAsync($"{api}/invitations", new { email = "x@example.com", role = "Viewer" })).StatusCode);
    }

    [Fact]
    public async Task Senza_la_sezione_Store_non_si_apre_niente_degli_store()
    {
        var owner = await _app.SignUpAsync();
        var project = await CreateProjectAsync(owner, "Solo social");
        var client = await InviteAsync(owner, "Admin", new { allProjects = true, sections = "Social" });
        var api = $"/api/v1/orgs/{owner.OrgId}";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/projects/{project}/listing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Client.GetAsync($"{api}/projects/{project}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Client.GetAsync($"{api}/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Client.GetAsync($"{api}/social/accounts")).StatusCode);

        // E viceversa: solo Store, niente Social.
        var store = await InviteAsync(owner, "Viewer", new { allProjects = true, sections = "Store" });
        Assert.Equal(HttpStatusCode.Forbidden, (await store.Client.GetAsync($"{api}/social/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await store.Client.GetAsync($"{api}/metrics")).StatusCode);
    }

    [Fact]
    public async Task L_accesso_si_cambia_dopo_ma_quello_di_un_owner_resta_completo()
    {
        var owner = await _app.SignUpAsync();
        var a = await CreateProjectAsync(owner, "A");
        var b = await CreateProjectAsync(owner, "B");
        var member = await InviteAsync(owner, "Viewer", null);
        var api = $"/api/v1/orgs/{owner.OrgId}";
        var memberId = (await (await owner.Client.GetAsync($"{api}/members")).ReadJsonAsync()).EnumerateArray()
            .Single(m => m.GetProperty("email").GetString() == member.Email).GetProperty("userId").GetGuid();

        // Senza accesso indicato, l'invitato vede tutto, come prima.
        Assert.Equal(2, (await (await member.Client.GetAsync($"{api}/projects")).ReadJsonAsync()).GetArrayLength());

        var changed = await owner.Client.PutAsJsonAsync($"{api}/members/{memberId}/access", new { allProjects = false, projectIds = new[] { b }, sections = "Social" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var projects = (await (await member.Client.GetAsync($"{api}/projects")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal([b], projects.Select(p => p.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.GetAsync($"{api}/metrics")).StatusCode);

        // Un progetto che non esiste, o nessuna sezione: no.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PutAsJsonAsync($"{api}/members/{memberId}/access", new { allProjects = false, projectIds = new[] { Guid.NewGuid() }, sections = "Social" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PutAsJsonAsync($"{api}/members/{memberId}/access", new { allProjects = true, sections = "None" })).StatusCode);

        var ownerId = (await (await owner.Client.GetAsync("/api/v1/me")).ReadJsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal("owner_access", await (await owner.Client.PutAsJsonAsync($"{api}/members/{ownerId}/access", new { allProjects = false, projectIds = new[] { a }, sections = "Social" })).ProblemCodeAsync());

        // Cancellato il progetto, sparisce anche dall'accesso del membro.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"{api}/projects/{b}")).StatusCode);
        var member2 = (await (await owner.Client.GetAsync($"{api}/members")).ReadJsonAsync()).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == memberId);
        Assert.Equal(0, member2.GetProperty("access").GetProperty("projectIds").GetArrayLength());
        Assert.Empty((await (await member.Client.GetAsync($"{api}/projects")).ReadJsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task I_post_ricorrenti_seguono_le_stesse_regole()
    {
        var owner = await _app.SignUpAsync();
        var mine = await CreateProjectAsync(owner, "A");
        var other = await CreateProjectAsync(owner, "B");
        var account = await ConnectMastodonAsync(owner);
        await owner.Client.PutAsJsonAsync($"/api/v1/orgs/{owner.OrgId}/social/accounts/{account}/projects", new { projectIds = new[] { mine, other } });
        var api = $"/api/v1/orgs/{owner.OrgId}";

        object Recurring(Guid? project) => new
        {
            text = "Ogni giorno", projectId = project, accountIds = new[] { account }, media = Array.Empty<object>(), frequency = "Daily", interval = 1,
            timeOfDay = "09:00", timeZone = "Europe/Rome", startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), isPaused = false
        };
        var otherId = (await (await owner.Client.PostAsJsonAsync($"{api}/social/recurring", Recurring(other))).ReadJsonAsync()).GetProperty("id").GetGuid();
        await owner.Client.PostAsJsonAsync($"{api}/social/recurring", Recurring(mine));

        var client = await InviteAsync(owner, "Admin", new { allProjects = false, projectIds = new[] { mine }, sections = "Social" });
        var list = (await (await client.Client.GetAsync($"{api}/social/recurring")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.Equal(mine, Assert.Single(list).GetProperty("projectId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.DeleteAsync($"{api}/social/recurring/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Client.PostAsJsonAsync($"{api}/social/recurring", Recurring(other))).StatusCode);
        Assert.Equal("project_required", await (await client.Client.PostAsJsonAsync($"{api}/social/recurring", Recurring(null))).ProblemCodeAsync());

        // Il calendario del progetto riceve solo le sue uscite.
        var from = Uri.EscapeDataString(DateTime.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(3).ToString("O"));
        var occurrences = (await (await owner.Client.GetAsync($"{api}/social/recurring/occurrences?from={from}&to={to}&projectId={other}")).ReadJsonAsync()).EnumerateArray().ToList();
        Assert.NotEmpty(occurrences);
        Assert.All(occurrences, o => Assert.Equal(otherId, o.GetProperty("recurringPostId").GetGuid()));
    }
}
