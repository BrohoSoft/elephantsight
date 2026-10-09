using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flarelytics.Tests.Integration;

/// <summary>Threads: login, pubblicazione (testo, immagini, carosello), rinnovo del token, importazione.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class ThreadsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Api = "https://graph.threads.net/v1.0";

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
    private FakeStoreServer Net => _app.SocialApis;

    /// <summary>Tutto il giro del login: start, ritorno da Threads con il codice, account collegato subito.</summary>
    private async Task<Guid> ConnectThreadsAsync(Account a)
    {
        Net.On(HttpMethod.Post, "^https://graph.threads.net/oauth/access_token$", """{"access_token":"token-breve","user_id":"7001"}""")
            .On(HttpMethod.Get, "^https://graph.threads.net/access_token$", """{"access_token":"token-th","token_type":"bearer","expires_in":5183944}""")
            .On(HttpMethod.Get, $"^{Api}/me$", """{"id":"7001","username":"meteo.app","name":"Meteo"}""");

        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/threads/start", null)).ReadJsonAsync();
        var url = new Uri(start.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("threads.net", url.Host);
        Assert.Equal("app-threads", query["client_id"]);
        Assert.Equal("http://app.test/social/threads/callback", query["redirect_uri"]);
        Assert.Equal("threads_basic,threads_content_publish", query["scope"]);

        var complete = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/threads/complete", new { code = "codice#_", state = query["state"].ToString() });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var exchange = QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "graph.threads.net/oauth/access_token").Last().Body);
        Assert.Equal("codice", exchange["code"]);
        Assert.Equal("segreto-threads", exchange["client_secret"]);
        Assert.Contains("th_exchange_token", Net.Calls(HttpMethod.Get, "graph.threads.net/access_token").Last().Url);

        var account = await complete.ReadJsonAsync();
        Assert.Equal("Threads", account.GetProperty("network").GetString());
        Assert.Equal("@meteo.app", account.GetProperty("handle").GetString());
        Assert.Equal(500, account.GetProperty("limits").GetProperty("maxCharacters").GetInt32());
        Assert.DoesNotContain("token-th", account.GetRawText());
        return account.GetProperty("id").GetGuid();
    }

    private async Task<Guid> UploadImageAsync(Account a)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestJpeg.Create(1080, 1080));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "foto.jpg");
        return (await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", form)).ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> CreatePostAsync(Account a, string text, Guid account, params Guid[] media)
    {
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", new
        {
            text, scheduledAtUtc = DateTime.UtcNow.AddMinutes(-1), isDraft = false, accountIds = new[] { account },
            media = media.Select(id => new { id, altText = "Il radar" }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> TargetAsync(Account a, JsonElement post) =>
        (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}")).ReadJsonAsync())
        .GetProperty("targets")[0];

    private void PublishingWorks() =>
        Net.On(HttpMethod.Get, $"^{Api}/c\\d+$", """{"status":"FINISHED"}""")
            .On(HttpMethod.Post, $"^{Api}/7001/threads_publish$", """{"id":"t9"}""")
            .On(HttpMethod.Get, $"^{Api}/t9$", """{"permalink":"https://www.threads.net/@meteo.app/post/abc"}""");

    [Fact]
    public async Task Un_testo_esce_su_Threads_come_container_TEXT()
    {
        var a = await _app.SignUpAsync();
        var threads = await ConnectThreadsAsync(a);
        Net.On(HttpMethod.Post, $"^{Api}/7001/threads$", """{"id":"c1"}""");
        PublishingWorks();

        var post = await CreatePostAsync(a, "Radar aggiornato #meteo", threads);
        await Worker.RunOnceAsync(CancellationToken.None);

        var container = Net.Calls(HttpMethod.Post, "/7001/threads").Single(c => !c.Url.Contains("publish"));
        Assert.Contains("Bearer token-th", container.Headers);
        var fields = QueryHelpers.ParseQuery(container.Body);
        Assert.Equal("TEXT", fields["media_type"]);
        Assert.Equal("Radar aggiornato #meteo", fields["text"]);
        Assert.Equal("c1", QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "threads_publish").Single().Body)["creation_id"]);

        var target = await TargetAsync(a, post);
        Assert.Equal("Published", target.GetProperty("status").GetString());
        Assert.Equal("https://www.threads.net/@meteo.app/post/abc", target.GetProperty("externalUrl").GetString());
    }

    [Fact]
    public async Task Piu_immagini_diventano_un_carosello_scaricato_da_indirizzi_firmati()
    {
        var a = await _app.SignUpAsync();
        var threads = await ConnectThreadsAsync(a);
        var counter = 0;
        // Un id per ogni container, nell'ordine in cui si creano.
        Net.On(HttpMethod.Post, $"^{Api}/7001/threads$", _ => $$"""{"id":"c{{++counter}}"}""");
        PublishingWorks();
        var first = await UploadImageAsync(a);
        var second = await UploadImageAsync(a);

        await CreatePostAsync(a, "Due radar", threads, first, second);
        await Worker.RunOnceAsync(CancellationToken.None);

        var containers = Net.Calls(HttpMethod.Post, "/7001/threads").Where(c => !c.Url.Contains("publish")).Select(c => QueryHelpers.ParseQuery(c.Body)).ToList();
        Assert.Equal(3, containers.Count);
        Assert.All(containers.Take(2), c =>
        {
            Assert.Equal("IMAGE", c["media_type"]);
            Assert.Equal("true", c["is_carousel_item"]);
            Assert.StartsWith("https://tunnel.example/api/v1/social/media/", c["image_url"].ToString());
        });
        Assert.Equal("CAROUSEL", containers[2]["media_type"]);
        Assert.Equal("c1,c2", containers[2]["children"]);
        Assert.Equal("Due radar", containers[2]["text"]);
        Assert.Equal("c3", QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "threads_publish").Single().Body)["creation_id"]);
    }

    [Fact]
    public async Task Oltre_500_caratteri_non_si_programma()
    {
        var a = await _app.SignUpAsync();
        var threads = await ConnectThreadsAsync(a);
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", new
        {
            text = new string('a', 501), scheduledAtUtc = DateTime.UtcNow.AddHours(1), isDraft = false, accountIds = new[] { threads }, media = Array.Empty<object>()
        });
        Assert.Equal("post_invalid", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Il_token_di_Threads_si_rinnova_da_solo_e_uno_scaduto_chiede_di_ricollegare()
    {
        var a = await _app.SignUpAsync();
        var threads = await ConnectThreadsAsync(a);
        Net.On(HttpMethod.Get, "^https://graph.threads.net/refresh_access_token$", """{"access_token":"token-rinnovato","token_type":"bearer","expires_in":5183944}""")
            .On(HttpMethod.Get, $"^{Api}/me/threads$", """{"data":[]}""");

        await WithAccountAsync(a, threads, x => x.RenewToken(x.ProtectedSecret, DateTime.UtcNow.AddDays(30)));
        await Worker.RunOnceAsync(CancellationToken.None);

        var refresh = Net.Calls(HttpMethod.Get, "refresh_access_token").Single();
        Assert.Contains("grant_type=th_refresh_token", refresh.Url);
        Assert.Contains("access_token=token-th", refresh.Url);
        Assert.True((await WithAccountAsync(a, threads, _ => { })).TokenExpiresAtUtc > DateTime.UtcNow.AddDays(59));

        await WithAccountAsync(a, threads, x => x.RenewToken(x.ProtectedSecret, DateTime.UtcNow.AddMinutes(-1)));
        var post = await CreatePostAsync(a, "Ciao", threads);
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Contains("Threads è scaduto", (await TargetAsync(a, post)).GetProperty("error").GetString());
        Assert.Equal(SocialAccountStatus.NeedsReconnect, (await WithAccountAsync(a, threads, _ => { })).Status);
    }

    [Fact]
    public async Task I_post_usciti_fuori_da_ElephantSight_si_importano_senza_i_repost()
    {
        var a = await _app.SignUpAsync();
        await ConnectThreadsAsync(a);
        var at = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd'T'HH:mm:ss") + "+0000";
        Net.On(HttpMethod.Get, $"^{Api}/me/threads$", $$"""
                {"data":[
                  {"id":"p1","text":"Dall'app di Threads","media_type":"IMAGE","media_url":"https://cdn.threads.test/p1.jpg","permalink":"https://www.threads.net/@meteo.app/post/p1","timestamp":"{{at}}"},
                  {"id":"p2","media_type":"REPOST_FACADE","timestamp":"{{at}}"}
                ]}
                """)
            .OnBytes(HttpMethod.Get, "^https://cdn.threads.test/p1.jpg$", TestJpeg.Create(1080, 1080), "image/jpeg");

        await Worker.RunOnceAsync(CancellationToken.None);

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var posts = (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts?from={from}&to={to}")).ReadJsonAsync()).EnumerateArray().ToList();
        var post = Assert.Single(posts);
        Assert.Equal("Dall'app di Threads", post.GetProperty("text").GetString());
        Assert.True(post.GetProperty("imported").GetBoolean());
        Assert.Equal(1, post.GetProperty("media").GetArrayLength());
    }

    private async Task<SocialAccount> WithAccountAsync(Account a, Guid accountId, Action<SocialAccount> change)
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        var account = await db.Set<SocialAccount>().SingleAsync(x => x.Id == accountId);
        change(account);
        await db.SaveChangesAsync();
        return account;
    }
}
