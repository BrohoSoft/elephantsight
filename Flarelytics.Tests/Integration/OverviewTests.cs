using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>La panoramica: numeri dei social, prossimi post (anche ricorrenti), ultimi log, secondo l'accesso del membro.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class OverviewTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Mastodon = "https://mastodon.example";
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());
        _ = _app.Services;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task La_panoramica_conta_i_post_e_mette_insieme_programmati_e_ricorrenti()
    {
        var a = await _app.SignUpAsync();
        var api = $"/api/v1/orgs/{a.OrgId}";
        var mine = (await (await a.Client.PostAsJsonAsync($"{api}/projects", new { name = "App" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var other = (await (await a.Client.PostAsJsonAsync($"{api}/projects", new { name = "Cliente" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        _app.SocialApis.On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/verify_credentials$", """{"id":"42","username":"meteo","display_name":"Meteo"}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v2/instance$", """{"configuration":{"statuses":{"max_characters":500}}}""");
        var account = (await (await a.Client.PostAsJsonAsync($"{api}/social/accounts/mastodon", new { instanceUrl = "mastodon.example", accessToken = "t" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await a.Client.PutAsJsonAsync($"{api}/social/accounts/{account}/projects", new { projectIds = new[] { mine, other } });

        object Post(string text, Guid project, double days, bool inbox = false) => new
        {
            text, scheduledAtUtc = DateTime.UtcNow.AddDays(days), isDraft = false, projectId = project, accountIds = inbox ? [] : new[] { account },
            media = Array.Empty<object>(), inbox
        };
        await a.Client.PostAsJsonAsync($"{api}/social/posts", Post("Fra due giorni", mine, 2));
        await a.Client.PostAsJsonAsync($"{api}/social/posts", Post("Fra cinque giorni", mine, 5));
        await a.Client.PostAsJsonAsync($"{api}/social/posts", Post("Del cliente", other, 1));
        await a.Client.PostAsJsonAsync($"{api}/social/posts", Post("Idea", mine, 0, inbox: true));
        await a.Client.PostAsJsonAsync($"{api}/social/recurring", new
        {
            text = "Ogni giorno", projectId = mine, accountIds = new[] { account }, media = Array.Empty<object>(), frequency = "Daily", interval = 1,
            timeOfDay = "09:00", timeZone = "Europe/Rome", startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), isPaused = false
        });

        var overview = await (await a.Client.GetAsync($"{api}/overview")).ReadJsonAsync();
        Assert.Equal(2, overview.GetProperty("projects").GetInt32());
        var social = overview.GetProperty("social");
        Assert.Equal(3, social.GetProperty("scheduled").GetInt32());
        Assert.Equal(1, social.GetProperty("inbox").GetInt32());
        Assert.Equal(1, social.GetProperty("recurringActive").GetInt32());
        var upcoming = social.GetProperty("upcoming").EnumerateArray().ToList();
        Assert.Equal(5, upcoming.Count);
        Assert.Equal(upcoming.Select(u => u.GetProperty("atUtc").GetDateTime()).Order(), upcoming.Select(u => u.GetProperty("atUtc").GetDateTime()));
        Assert.Contains(upcoming, u => u.GetProperty("recurringPostId").ValueKind == JsonValueKind.String);
        Assert.Equal(JsonValueKind.Array, overview.GetProperty("logs").ValueKind);

        // Nel progetto: solo i suoi.
        var project = (await (await a.Client.GetAsync($"{api}/overview?projectId={other}")).ReadJsonAsync()).GetProperty("social");
        Assert.Equal(1, project.GetProperty("scheduled").GetInt32());
        Assert.Equal(0, project.GetProperty("recurringActive").GetInt32());

        // Un membro con un progetto solo, senza gestione dell'organizzazione: i suoi numeri, niente log.
        var guest = await _app.SignUpAsync();
        await a.Client.PostAsJsonAsync($"{api}/invitations", new { email = guest.Email, role = "Viewer", access = new { allProjects = false, projectIds = new[] { other }, sections = "Social" } });
        await guest.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = _app.LinkToken(guest.Email) });
        var guestView = await (await guest.Client.GetAsync($"{api}/overview")).ReadJsonAsync();
        Assert.Equal(1, guestView.GetProperty("projects").GetInt32());
        Assert.Equal(1, guestView.GetProperty("social").GetProperty("scheduled").GetInt32());
        Assert.Equal(JsonValueKind.Null, guestView.GetProperty("logs").ValueKind);
    }
}
