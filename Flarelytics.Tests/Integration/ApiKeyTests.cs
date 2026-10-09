using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flarelytics.Tests.Integration;

/// <summary>Le chiavi API, l'API pubblica che riempie la coda e la coda "Da programmare" nel pannello.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class ApiKeyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Mastodon = "https://mastodon.example";

    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(connection);
        _ = _app.Services;
        _sync = new SyncHost(_app, connection, backfillDays: 1);
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _app.DisposeAsync();
    }

    private SocialPublishWorker Worker => _sync.Services.GetServices<IHostedService>().OfType<SocialPublishWorker>().Single();

    private async Task<string> CreateKeyAsync(Account a, string name = "CMS del sito")
    {
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/api-keys", new { name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var secret = (await response.ReadJsonAsync()).GetProperty("secret").GetString()!;
        Assert.StartsWith("wsk_", secret);
        return secret;
    }

    private HttpClient ApiClient(string key)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<Guid> UploadAsync(HttpClient api, int width = 1080, int height = 1080)
    {
        var file = new ByteArrayContent(TestJpeg.Create(width, height));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var response = await api.PostAsync("/api/v1/public/media", new MultipartFormDataContent { { file, "file", "foto.jpg" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ConnectMastodonAsync(Account a)
    {
        _app.SocialApis.On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/verify_credentials$", """{"id":"42","username":"meteo","display_name":"Meteo"}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v2/instance$", """{"configuration":{"statuses":{"max_characters":500}}}""");
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/mastodon", new { instanceUrl = Mastodon, accessToken = "t" });
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> InboxAsync(Account a) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/inbox")).ReadJsonAsync();

    [Fact]
    public async Task La_chiave_si_vede_una_volta_e_a_database_resta_solo_l_hash()
    {
        var a = await _app.SignUpAsync();
        var secret = await CreateKeyAsync(a);

        var list = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/api-keys")).ReadJsonAsync();
        var key = list.EnumerateArray().Single();
        Assert.Equal(secret[..12], key.GetProperty("prefix").GetString());
        Assert.Equal("Mario Rossi", key.GetProperty("createdBy").GetString());
        Assert.DoesNotContain(secret, list.GetRawText());

        using var scope = _app.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<ApiKey>().SingleAsync();
        Assert.Equal(ApiKey.Hash(secret), stored.KeyHash);
    }

    [Fact]
    public async Task Senza_chiave_con_una_sbagliata_o_revocata_si_risponde_401()
    {
        var a = await _app.SignUpAsync();
        var secret = await CreateKeyAsync(a);
        var body = new { text = "Ciao" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().PostAsJsonAsync("/api/v1/public/posts", body)).StatusCode);
        var wrong = await ApiClient("wsk_" + new string('x', 43)).PostAsJsonAsync("/api/v1/public/posts", body);
        Assert.Equal("invalid_api_key", await wrong.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, (await ApiClient(secret).PostAsJsonAsync("/api/v1/public/posts", body)).StatusCode);

        var keyId = (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/api-keys")).ReadJsonAsync())[0].GetProperty("id").GetGuid();
        await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/api-keys/{keyId}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiClient(secret).PostAsJsonAsync("/api/v1/public/posts", body)).StatusCode);

        // Una chiave API non apre le rotte del pannello, e non le fa esplodere.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiClient(secret).GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts")).StatusCode);
    }

    [Fact]
    public async Task Un_post_dall_api_va_in_coda_non_nel_calendario_e_con_lo_stesso_riferimento_non_si_duplica()
    {
        var a = await _app.SignUpAsync();
        var api = ApiClient(await CreateKeyAsync(a));
        var image = await UploadAsync(api);
        var suggested = DateTime.UtcNow.AddDays(2);
        var body = new { text = "Nuovo articolo sul blog", suggestedAtUtc = suggested, externalRef = "cms-42", media = new[] { new { id = image, altText = "Copertina" } } };

        var created = await api.PostAsJsonAsync("/api/v1/public/posts", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var post = await created.ReadJsonAsync();
        Assert.Equal("Inbox", post.GetProperty("status").GetString());
        Assert.Equal("Copertina", post.GetProperty("media")[0].GetProperty("altText").GetString());

        var again = await api.PostAsJsonAsync("/api/v1/public/posts", body);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(post.GetProperty("id").GetGuid(), (await again.ReadJsonAsync()).GetProperty("id").GetGuid());

        var inbox = await InboxAsync(a);
        Assert.Equal(1, inbox.GetArrayLength());
        Assert.Equal("CMS del sito", inbox[0].GetProperty("source").GetString());
        Assert.Equal(suggested, inbox[0].GetProperty("suggestedAtUtc").GetDateTime(), TimeSpan.FromSeconds(1));

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(10).ToString("O"));
        Assert.Equal(0, (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts?from={from}&to={to}")).ReadJsonAsync()).GetArrayLength());

        // Un'immagine già usata non si riusa in un altro post.
        var reuse = await api.PostAsJsonAsync("/api/v1/public/posts", new { text = "Altro", media = new[] { new { id = image } } });
        Assert.Equal("media_not_found", await reuse.ProblemCodeAsync());
    }

    [Fact]
    public async Task Dalla_coda_si_assegnano_piu_post_agli_account_e_quelli_che_non_vanno_restano_col_motivo()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var api = ApiClient(await CreateKeyAsync(a));
        var suggested = DateTime.UtcNow.AddMinutes(-1);

        var ok = await (await api.PostAsJsonAsync("/api/v1/public/posts", new { text = "Breve", suggestedAtUtc = suggested })).ReadJsonAsync();
        var tooLong = await (await api.PostAsJsonAsync("/api/v1/public/posts", new { text = new string('a', 600), suggestedAtUtc = suggested })).ReadJsonAsync();
        var noDate = await (await api.PostAsJsonAsync("/api/v1/public/posts", new { text = "Senza data" })).ReadJsonAsync();

        var assign = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/inbox/assign", new
        {
            postIds = new[] { ok, tooLong, noDate }.Select(p => p.GetProperty("id").GetGuid()), accountIds = new[] { mastodon }, scheduledAtUtc = (DateTime?)null
        });
        var results = (await assign.ReadJsonAsync()).EnumerateArray().ToDictionary(r => r.GetProperty("postId").GetGuid());
        Assert.True(results[ok.GetProperty("id").GetGuid()].GetProperty("scheduled").GetBoolean());
        Assert.Contains("500", results[tooLong.GetProperty("id").GetGuid()].GetProperty("problem").GetString());
        Assert.Contains("data", results[noDate.GetProperty("id").GetGuid()].GetProperty("problem").GetString());
        Assert.Equal(2, (await InboxAsync(a)).GetArrayLength());

        // Quello assegnato è un post programmato come gli altri: all'ora proposta esce.
        _app.SocialApis.On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"id":"s1","url":"https://mastodon.example/@meteo/s1"}""");
        await Worker.RunOnceAsync(CancellationToken.None);
        var status = await (await api.GetAsync($"/api/v1/public/posts/{ok.GetProperty("id").GetGuid()}")).ReadJsonAsync();
        Assert.Equal("Published", status.GetProperty("status").GetString());
        Assert.Equal("https://mastodon.example/@meteo/s1", status.GetProperty("targets")[0].GetProperty("externalUrl").GetString());

        // Ormai non si ritira più dall'API; quelli ancora in coda sì.
        Assert.Equal("post_scheduled", await (await api.DeleteAsync($"/api/v1/public/posts/{ok.GetProperty("id").GetGuid()}")).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/v1/public/posts/{noDate.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Un_post_della_coda_si_modifica_dal_pannello_e_ne_esce_solo_quando_si_programma()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var api = ApiClient(await CreateKeyAsync(a));
        var post = await (await api.PostAsJsonAsync("/api/v1/public/posts", new { text = "Bozza dal CMS" })).ReadJsonAsync();
        var id = post.GetProperty("id").GetGuid();
        object Save(bool draft) => new
        {
            text = "Corretto a mano", scheduledAtUtc = DateTime.UtcNow.AddDays(1), isDraft = draft, projectId = (Guid?)null,
            accountIds = new[] { mastodon }, media = Array.Empty<object>(), overrides = (object?)null
        };

        Assert.Equal(HttpStatusCode.OK, (await a.Client.PutAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{id}", Save(draft: true))).StatusCode);
        var inbox = await InboxAsync(a);
        Assert.Equal("Corretto a mano", inbox[0].GetProperty("text").GetString());

        var scheduled = await (await a.Client.PutAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{id}", Save(draft: false))).ReadJsonAsync();
        Assert.Equal("Scheduled", scheduled.GetProperty("status").GetString());
        Assert.Equal(0, (await InboxAsync(a)).GetArrayLength());
    }

    [Fact]
    public async Task La_chiave_di_un_organizzazione_non_vede_i_post_di_un_altra()
    {
        var a = await _app.SignUpAsync();
        var b = await _app.SignUpAsync();
        var postOfA = await (await ApiClient(await CreateKeyAsync(a)).PostAsJsonAsync("/api/v1/public/posts", new { text = "Di A" })).ReadJsonAsync();

        var apiB = ApiClient(await CreateKeyAsync(b));
        Assert.Equal(HttpStatusCode.NotFound, (await apiB.GetAsync($"/api/v1/public/posts/{postOfA.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(0, (await InboxAsync(b)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync($"/api/v1/orgs/{b.OrgId}/api-keys/{Guid.NewGuid()}")).StatusCode);
    }
}
