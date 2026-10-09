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

/// <summary>I video: caricamento (anche a pezzi), Reel su Instagram, TikTok.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class VideoPostTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string InstagramGraph = "https://graph.instagram.com/v26.0";
    private const string TikTok = "https://open.tiktokapis.com";

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

    private FakeStoreServer Net => _app.SocialApis;
    private SocialPublishWorker Worker => _sync.Services.GetServices<IHostedService>().OfType<SocialPublishWorker>().Single();

    private static async Task<JsonElement> UploadAsync(Account a, byte[] file, string name = "video.mp4")
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var response = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", new MultipartFormDataContent { { content, "file", name } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static object Post(string text, Guid[] accounts, Guid[] media, object? options = null) => new
    {
        text, scheduledAtUtc = DateTime.UtcNow.AddMinutes(-1), isDraft = false, projectId = (Guid?)null, accountIds = accounts,
        media = media.Select(id => new { id, altText = (string?)null }), overrides = (object?)null, options
    };

    private async Task<Guid> ConnectInstagramAsync(Account a)
    {
        Net.On(HttpMethod.Post, "^https://api.instagram.com/oauth/access_token$", """{"data":[{"access_token":"breve","user_id":"17841"}]}""")
            .On(HttpMethod.Get, "^https://graph.instagram.com/access_token$", """{"access_token":"token-ig","expires_in":5183944}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/me$", """{"user_id":"17841","username":"meteo.app","account_type":"BUSINESS"}""");
        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/instagram/start", null)).ReadJsonAsync();
        var state = QueryHelpers.ParseQuery(new Uri(start.GetProperty("url").GetString()!).Query)["state"].ToString();
        var account = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/instagram/complete", new { code = "c", state })).ReadJsonAsync();
        return account.GetProperty("id").GetGuid();
    }

    private async Task<Guid> ConnectTikTokAsync(Account a, string privacy = "SELF_ONLY")
    {
        Net.On(HttpMethod.Post, $"^{TikTok}/v2/oauth/token/$",
                """{"access_token":"tk-access","expires_in":86400,"refresh_token":"tk-refresh","refresh_expires_in":31536000,"open_id":"open-1","scope":"user.info.basic,video.publish","token_type":"Bearer"}""")
            .On(HttpMethod.Post, $"^{TikTok}/v2/post/publish/creator_info/query/$", $$$"""
                {"data":{"creator_username":"meteoapp","creator_nickname":"Meteo","privacy_level_options":["{{{privacy}}}"],
                 "comment_disabled":false,"duet_disabled":false,"stitch_disabled":true,"max_video_post_duration_sec":300},
                 "error":{"code":"ok","message":"","log_id":"l"}}
                """);

        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/tiktok/start", null)).ReadJsonAsync();
        var url = new Uri(start.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("www.tiktok.com", url.Host);
        Assert.Equal("chiave-tiktok", query["client_key"]);
        Assert.Equal("user.info.basic,video.publish", query["scope"]);
        Assert.Equal("http://app.test/social/tiktok/callback", query["redirect_uri"]);

        var complete = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/tiktok/complete", new { code = "codice", state = query["state"].ToString() });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var account = await complete.ReadJsonAsync();
        Assert.Equal("@meteoapp", account.GetProperty("handle").GetString());
        Assert.DoesNotContain("tk-refresh", account.GetRawText());
        return account.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetPostAsync(Account a, JsonElement post) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}")).ReadJsonAsync();

    [Fact]
    public async Task Un_video_grande_si_carica_a_pezzi_e_rimandare_un_pezzo_non_lo_duplica()
    {
        var a = await _app.SignUpAsync();
        var video = TestVideo.Create(1080, 1920, 42_000, padding: 300_000);
        var start = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads", new { fileName = "reel.mp4", size = video.Length })).ReadJsonAsync();
        var id = start.GetProperty("uploadId").GetGuid();

        async Task SendAsync(int from, int length)
        {
            var response = await a.Client.PutAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{id}?offset={from}", new ByteArrayContent(video[from..(from + length)]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await SendAsync(0, 150_000);
        await SendAsync(0, 150_000);                      // ripetuto dopo un errore di rete
        await SendAsync(150_000, video.Length - 150_000);

        var done = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{id}/complete", new { fileName = "reel.mp4" })).ReadJsonAsync();
        Assert.Equal("Video", done.GetProperty("kind").GetString());
        Assert.Equal(42_000, done.GetProperty("durationMs").GetInt32());
        Assert.Equal(video.Length, done.GetProperty("sizeBytes").GetInt64());
        Assert.EndsWith(".mp4", new Uri("http://x" + done.GetProperty("url").GetString()).AbsolutePath);

        // Il file servito con l'indirizzo firmato è quello caricato, e si legge anche a pezzi.
        var served = await _app.CreateClient().GetAsync(done.GetProperty("url").GetString());
        Assert.Equal("video/mp4", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(video, await served.Content.ReadAsByteArrayAsync());

        var notVideo = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads", new { fileName = "x.bin", size = 10 })).ReadJsonAsync();
        var other = notVideo.GetProperty("uploadId").GetGuid();
        await a.Client.PutAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{other}?offset=0", new ByteArrayContent(new byte[10]));
        Assert.Equal("file_type", await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{other}/complete", new { fileName = "x.bin" })).ProblemCodeAsync());
    }

    [Fact]
    public async Task Un_video_su_Instagram_diventa_un_Reel_anche_fuori_dalla_griglia_e_si_aspetta_l_elaborazione()
    {
        var a = await _app.SignUpAsync();
        var instagram = await ConnectInstagramAsync(a);
        var video = await UploadAsync(a, TestVideo.Create(1080, 1920, 20_000));
        Net.On(HttpMethod.Post, $"^{InstagramGraph}/17841/media$", """{"id":"reel-1"}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/reel-1$", """{"status_code":"IN_PROGRESS"}""")
            .On(HttpMethod.Post, $"^{InstagramGraph}/17841/media_publish$", """{"id":"m9"}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/m9$", """{"permalink":"https://www.instagram.com/reel/m9/"}""");

        var post = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts",
            Post("Il radar in 20 secondi", [instagram], [video.GetProperty("id").GetGuid()], new { instagramShowInGrid = false }))).ReadJsonAsync();
        await Worker.RunOnceAsync(CancellationToken.None);

        var container = QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "/17841/media").Single(c => !c.Url.Contains("publish")).Body);
        Assert.Equal("REELS", container["media_type"]);
        Assert.Equal("false", container["share_to_feed"]);
        Assert.Contains(".mp4?e=", container["video_url"].ToString());
        Assert.StartsWith("https://tunnel.example/", container["video_url"].ToString());

        // Instagram sta ancora elaborando: niente errore, niente tentativo consumato, si ripassa.
        var waiting = (await GetPostAsync(a, post)).GetProperty("targets")[0];
        Assert.Equal("Pending", waiting.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("error").ValueKind);
        Assert.Empty(Net.Calls(HttpMethod.Post, "media_publish"));

        Net.On(HttpMethod.Get, $"^{InstagramGraph}/reel-1$", """{"status_code":"FINISHED"}""");
        await DueNowAsync(a);
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Single(Net.Calls(HttpMethod.Post, "/17841/media").Where(c => !c.Url.Contains("publish"))); // stesso container, non un secondo
        var published = (await GetPostAsync(a, post)).GetProperty("targets")[0];
        Assert.Equal("Published", published.GetProperty("status").GetString());
        Assert.Equal("https://www.instagram.com/reel/m9/", published.GetProperty("externalUrl").GetString());
    }

    [Fact]
    public async Task Un_video_senza_indice_in_testa_non_si_programma_come_Reel()
    {
        var a = await _app.SignUpAsync();
        var instagram = await ConnectInstagramAsync(a);
        var video = await UploadAsync(a, TestVideo.Create(1080, 1920, 20_000, fastStart: false));

        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", Post("Reel", [instagram], [video.GetProperty("id").GetGuid()]));
        Assert.Contains("faststart", (await response.ReadJsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Su_TikTok_il_video_si_carica_a_pezzi_con_le_scelte_della_persona()
    {
        var a = await _app.SignUpAsync();
        var tiktok = await ConnectTikTokAsync(a);
        var creator = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/{tiktok}/tiktok-creator")).ReadJsonAsync();
        Assert.Equal(["SELF_ONLY"], creator.GetProperty("privacyLevels").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(creator.GetProperty("stitchDisabled").GetBoolean());

        var file = TestVideo.Create(1080, 1920, 30_000);
        var video = await UploadAsync(a, file);
        var media = new[] { video.GetProperty("id").GetGuid() };

        // Senza la visibilità scelta non si programma: TikTok vuole che la scelga la persona.
        var noPrivacy = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", Post("Ciao TikTok", [tiktok], media));
        Assert.Contains("chi può vedere", (await noPrivacy.ReadJsonAsync()).GetProperty("detail").GetString());

        Net.On(HttpMethod.Post, $"^{TikTok}/v2/post/publish/video/init/$",
                """{"data":{"publish_id":"v_pub_1","upload_url":"https://upload.tiktok.test/video/"},"error":{"code":"ok"}}""")
            .On(HttpMethod.Put, "^https://upload.tiktok.test/video/$", "", HttpStatusCode.Created)
            .On(HttpMethod.Post, $"^{TikTok}/v2/post/publish/status/fetch/$", """{"data":{"status":"PUBLISH_COMPLETE","publicaly_available_post_id":[]},"error":{"code":"ok"}}""");

        var post = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts",
            Post("Ciao TikTok #meteo", [tiktok], media, new { tikTokPrivacy = "SELF_ONLY", tikTokAllowComment = true, tikTokAllowStitch = true }))).ReadJsonAsync();
        await Worker.RunOnceAsync(CancellationToken.None);

        var init = JsonDocument.Parse(Net.Calls(HttpMethod.Post, "/video/init/").Single().Body!).RootElement;
        var info = init.GetProperty("post_info");
        Assert.Equal("Ciao TikTok #meteo", info.GetProperty("title").GetString());
        Assert.Equal("SELF_ONLY", info.GetProperty("privacy_level").GetString());
        Assert.False(info.GetProperty("disable_comment").GetBoolean());  // scelto dalla persona
        Assert.True(info.GetProperty("disable_duet").GetBoolean());      // spento di default
        Assert.True(info.GetProperty("disable_stitch").GetBoolean());    // il creator l'ha spento: vince lui
        var source = init.GetProperty("source_info");
        Assert.Equal("FILE_UPLOAD", source.GetProperty("source").GetString());
        Assert.Equal(file.Length, source.GetProperty("video_size").GetInt64());
        Assert.Equal(1, source.GetProperty("total_chunk_count").GetInt32());

        var upload = Net.Calls(HttpMethod.Put, "upload.tiktok.test").Single();
        Assert.Equal(file, upload.Bytes);
        Assert.Contains("Bearer tk-access", Net.Calls(HttpMethod.Post, "/video/init/").Single().Headers);

        var target = (await GetPostAsync(a, post)).GetProperty("targets")[0];
        Assert.Equal("Published", target.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Il_token_di_TikTok_si_rinnova_prima_di_pubblicare()
    {
        var a = await _app.SignUpAsync();
        var tiktok = await ConnectTikTokAsync(a);
        using (var scope = _app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            var account = await db.Set<SocialAccount>().SingleAsync();
            account.RenewToken(account.ProtectedSecret, DateTime.UtcNow.AddMinutes(1)); // sta per scadere
            await db.SaveChangesAsync();
        }

        Net.On(HttpMethod.Post, $"^{TikTok}/v2/oauth/token/$",
                """{"access_token":"tk-nuovo","expires_in":86400,"refresh_token":"tk-refresh-2","refresh_expires_in":31536000,"open_id":"open-1"}""")
            .On(HttpMethod.Post, $"^{TikTok}/v2/post/publish/video/init/$", """{"data":{"publish_id":"p2","upload_url":"https://upload.tiktok.test/v2/"},"error":{"code":"ok"}}""")
            .On(HttpMethod.Put, "^https://upload.tiktok.test/v2/$", "", HttpStatusCode.Created)
            .On(HttpMethod.Post, $"^{TikTok}/v2/post/publish/status/fetch/$", """{"data":{"status":"PROCESSING_UPLOAD"},"error":{"code":"ok"}}""");

        var video = await UploadAsync(a, TestVideo.Create(1080, 1920, 10_000));
        await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts",
            Post("Rinnovo", [tiktok], [video.GetProperty("id").GetGuid()], new { tikTokPrivacy = "SELF_ONLY" }));
        await Worker.RunOnceAsync(CancellationToken.None);

        var refresh = Net.Calls(HttpMethod.Post, "/v2/oauth/token/").Last();
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=tk-refresh", refresh.Body);
        Assert.Contains("Bearer tk-nuovo", Net.Calls(HttpMethod.Post, "/video/init/").Single().Headers);
    }

    [Fact]
    public async Task Dall_api_pubblica_un_video_arriva_nel_blocco_con_il_suo_tipo()
    {
        var a = await _app.SignUpAsync();
        var secret = (await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/api-keys", new { name = "Editor video" })).ReadJsonAsync()).GetProperty("secret").GetString()!;
        var api = _app.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var posts = JsonSerializer.Serialize(new object[]
        {
            new { type = "video", text = "Il reel della settimana", externalRef = "r1", media = new[] { new { file = "clip" } }, options = new { showInProfileGrid = false } },
            new { type = "video", text = "Questa è una foto", externalRef = "r2", media = new[] { new { file = "foto" } } },
        });
        var clip = new ByteArrayContent(TestVideo.Create(1080, 1920, 15_000));
        var foto = new ByteArrayContent(TestJpeg.Create(1080, 1080));
        var response = await api.PostAsync("/api/v1/public/posts/batch",
            new MultipartFormDataContent { { new StringContent(posts), "posts" }, { clip, "clip", "clip.mp4" }, { foto, "foto", "foto.jpg" } });
        var results = (await response.ReadJsonAsync()).GetProperty("results");

        Assert.Equal("created", results[0].GetProperty("outcome").GetString());
        var created = results[0].GetProperty("post");
        Assert.Equal("Video", created.GetProperty("media")[0].GetProperty("kind").GetString());
        Assert.False(created.GetProperty("options").GetProperty("instagramShowInGrid").GetBoolean());
        Assert.Equal("rejected", results[1].GetProperty("outcome").GetString());
        Assert.Contains("video", results[1].GetProperty("error").GetString());
    }

    /// <summary>Il prossimo controllo dell'elaborazione è fra 30 secondi: nei test lo si anticipa.</summary>
    private async Task DueNowAsync(Account a)
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
        await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<SocialPostTarget>()
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1)));
    }
}
