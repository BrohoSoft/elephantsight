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

/// <summary>Il calendario dei social: account, post programmati, pubblicazione del worker su ogni rete.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SocialTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Graph = "https://graph.facebook.com/v26.0";
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
    private FakeStoreServer Net => _app.SocialApis;

    // --- collegamento degli account ---

    private async Task<Guid> ConnectBlueskyAsync(Account a)
    {
        Net.On(HttpMethod.Post, "^https://bsky.social/xrpc/com.atproto.server.createSession$",
            """{"accessJwt":"jwt-1","refreshJwt":"r","did":"did:plc:meteo","handle":"meteo.bsky.social"}""");
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/bluesky",
            new { handle = "@meteo.bsky.social", appPassword = "abcd-efgh-ijkl-mnop" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ConnectMastodonAsync(Account a)
    {
        Net.On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/verify_credentials$", """{"id":"42","username":"meteo","display_name":"Meteo"}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v2/instance$", """{"configuration":{"statuses":{"max_characters":1000}}}""");
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/mastodon",
            new { instanceUrl = "mastodon.example/", accessToken = "token-mastodon" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Tutto il giro del login Meta: start, ritorno da Facebook, scelta degli account.</summary>
    private async Task<(Guid Page, Guid Instagram)> ConnectMetaAsync(Account a)
    {
        Net.On(HttpMethod.Get, $"^{Graph}/oauth/access_token$", """{"access_token":"token-utente","token_type":"bearer"}""")
            .On(HttpMethod.Get, $"^{Graph}/me/accounts$",
                """{"data":[{"id":"p1","name":"Meteo","access_token":"token-pagina","instagram_business_account":{"id":"ig1","username":"meteo.app"}}]}""");

        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/meta/start", null)).ReadJsonAsync();
        var url = new Uri(start.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("app-meta", query["client_id"]);
        Assert.Equal("http://app.test/social/meta/callback", query["redirect_uri"]);

        var complete = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/meta/complete", new { code = "codice", state = query["state"].ToString() });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var candidates = await complete.ReadJsonAsync();
        Assert.Equal(["fb:p1", "ig:ig1"], candidates.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToArray());
        Assert.DoesNotContain("token-pagina", candidates.GetRawText()); // la scelta è cifrata

        var added = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/meta/accounts",
            new { selection = candidates.GetProperty("selection").GetString(), keys = new[] { "fb:p1", "ig:ig1" } })).ReadJsonAsync();
        var ids = added.EnumerateArray().ToDictionary(x => x.GetProperty("network").GetString()!, x => x.GetProperty("id").GetGuid());
        return (ids["FacebookPage"], ids["Instagram"]);
    }

    private const string InstagramGraph = "https://graph.instagram.com/v26.0";

    /// <summary>Instagram Login: start, ritorno da Instagram con il codice, account collegato subito.</summary>
    private async Task<Guid> ConnectInstagramLoginAsync(Account a, string accountType = "BUSINESS", HttpStatusCode expected = HttpStatusCode.OK)
    {
        Net.On(HttpMethod.Post, "^https://api.instagram.com/oauth/access_token$",
                """{"data":[{"access_token":"token-breve","user_id":"17841","permissions":"instagram_business_basic,instagram_business_content_publish"}]}""")
            .On(HttpMethod.Get, "^https://graph.instagram.com/access_token$", """{"access_token":"token-ig","token_type":"bearer","expires_in":5183944}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/me$", $$"""{"user_id":"17841","username":"meteo.app","name":"Meteo","account_type":"{{accountType}}"}""");

        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/instagram/start", null)).ReadJsonAsync();
        var url = new Uri(start.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("www.instagram.com", url.Host);
        Assert.Equal("app-instagram", query["client_id"]);
        Assert.Equal("http://app.test/social/instagram/callback", query["redirect_uri"]);

        // Instagram aggiunge "#_" in fondo al codice: va tolto prima dello scambio.
        var complete = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/instagram/complete", new { code = "codice#_", state = query["state"].ToString() });
        Assert.Equal(expected, complete.StatusCode);
        if (expected != HttpStatusCode.OK) return Guid.Empty;

        var exchange = QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "api.instagram.com/oauth/access_token").Last().Body);
        Assert.Equal("codice", exchange["code"]);
        Assert.Equal("segreto-instagram", exchange["client_secret"]);
        Assert.Contains("ig_exchange_token", Net.Calls(HttpMethod.Get, "graph.instagram.com/access_token").Last().Url);

        var account = await complete.ReadJsonAsync();
        Assert.Equal("@meteo.app", account.GetProperty("handle").GetString());
        Assert.DoesNotContain("token-ig", account.GetRawText());
        return account.GetProperty("id").GetGuid();
    }

    private async Task<Guid> UploadImageAsync(Account a, int width = 1080, int height = 1080)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestJpeg.Create(width, height));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "foto.jpg");
        var response = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var media = await response.ReadJsonAsync();
        Assert.Equal(width, media.GetProperty("width").GetInt32());
        return media.GetProperty("id").GetGuid();
    }

    private static object Post(string text, DateTime at, Guid[] accounts, Guid[]? media = null, bool draft = false, object? overrides = null) => new
    {
        text, scheduledAtUtc = at, isDraft = draft, projectId = (Guid?)null, accountIds = accounts,
        media = (media ?? []).Select(id => new { id, altText = "Il radar sopra l'Italia" }).ToArray(), overrides
    };

    private static async Task<JsonElement> CreatePostAsync(Account a, object body)
    {
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> GetPostAsync(Account a, JsonElement post) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}")).ReadJsonAsync();

    private static JsonElement Target(JsonElement post, string network) =>
        post.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("network").GetString() == network);

    // --- test ---

    [Fact]
    public async Task Il_segreto_dell_account_non_esce_e_sul_database_e_cifrato()
    {
        var a = await _app.SignUpAsync();
        await ConnectBlueskyAsync(a);

        var list = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/accounts")).ReadJsonAsync();
        var account = list.EnumerateArray().Single();
        Assert.Equal("meteo.bsky.social", account.GetProperty("handle").GetString());
        Assert.Equal(300, account.GetProperty("limits").GetProperty("maxCharacters").GetInt32());
        Assert.DoesNotContain("abcd-efgh", list.GetRawText());

        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
        var stored = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<SocialAccount>().SingleAsync();
        Assert.DoesNotContain("abcd-efgh", stored.ProtectedSecret);
        Assert.Equal("did:plc:meteo", stored.ExternalId);
    }

    [Fact]
    public async Task Un_post_esce_su_Bluesky_solo_alla_sua_ora_con_immagine_link_e_hashtag()
    {
        var a = await _app.SignUpAsync();
        var bluesky = await ConnectBlueskyAsync(a);
        var image = await UploadImageAsync(a, 1200, 800);
        Net.On(HttpMethod.Post, "/xrpc/com.atproto.repo.uploadBlob$", """{"blob":{"$type":"blob","ref":{"$link":"bafk1"},"mimeType":"image/jpeg","size":200}}""")
            .On(HttpMethod.Post, "/xrpc/com.atproto.repo.createRecord$", """{"uri":"at://did:plc:meteo/app.bsky.feed.post/3kabc","cid":"c1"}""");

        const string text = "Novità: radar più veloce https://meteo.it/radar. #meteo";
        var post = await CreatePostAsync(a, Post(text, DateTime.UtcNow.AddHours(1), [bluesky], [image]));
        Assert.Equal("Scheduled", post.GetProperty("status").GetString());

        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(Net.Calls(HttpMethod.Post, "createRecord")); // non è ancora ora

        var update = await a.Client.PutAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}",
            Post(text, DateTime.UtcNow.AddMinutes(-1), [bluesky], [image]));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(TestJpeg.Create(1200, 800), Net.Calls(HttpMethod.Post, "uploadBlob").Single().Bytes);
        var record = JsonDocument.Parse(Net.Calls(HttpMethod.Post, "createRecord").Single().Body!).RootElement;
        Assert.Equal("did:plc:meteo", record.GetProperty("repo").GetString());
        var r = record.GetProperty("record");
        Assert.Equal(text, r.GetProperty("text").GetString());

        // "Novità" ha una "à" di due byte: gli estremi dei facet sono in byte, non in caratteri.
        var facets = r.GetProperty("facets").EnumerateArray().ToList();
        var link = facets.Single(f => f.GetProperty("features")[0].GetProperty("$type").GetString()!.EndsWith("#link"));
        Assert.Equal("https://meteo.it/radar", link.GetProperty("features")[0].GetProperty("uri").GetString()); // senza il punto finale
        Assert.Equal(27, link.GetProperty("index").GetProperty("byteStart").GetInt32());
        Assert.Equal(49, link.GetProperty("index").GetProperty("byteEnd").GetInt32());
        Assert.Contains(facets, f => f.GetProperty("features")[0].TryGetProperty("tag", out var tag) && tag.GetString() == "meteo");

        var embedded = r.GetProperty("embed").GetProperty("images")[0];
        Assert.Equal("Il radar sopra l'Italia", embedded.GetProperty("alt").GetString());
        Assert.Equal(1200, embedded.GetProperty("aspectRatio").GetProperty("width").GetInt32());

        var published = await GetPostAsync(a, post);
        Assert.Equal("Published", published.GetProperty("status").GetString());
        Assert.Equal("https://bsky.app/profile/did:plc:meteo/post/3kabc", Target(published, "Bluesky").GetProperty("externalUrl").GetString());
        Assert.False(published.GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Una_bozza_non_si_pubblica_e_non_si_controlla()
    {
        var a = await _app.SignUpAsync();
        var bluesky = await ConnectBlueskyAsync(a);

        // 400 caratteri: troppi per Bluesky, ma una bozza si salva lo stesso.
        var post = await CreatePostAsync(a, Post(new string('a', 400), DateTime.UtcNow.AddMinutes(-5), [bluesky], draft: true));
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Draft", (await GetPostAsync(a, post)).GetProperty("status").GetString());
        Assert.Empty(Net.Calls(HttpMethod.Post, "createRecord"));
    }

    [Fact]
    public async Task I_limiti_delle_reti_si_controllano_quando_si_programma()
    {
        var a = await _app.SignUpAsync();
        var bluesky = await ConnectBlueskyAsync(a);
        var (_, instagram) = await ConnectMetaAsync(a);

        var tooLong = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", Post(new string('a', 301), DateTime.UtcNow.AddHours(1), [bluesky]));
        Assert.Equal("post_invalid", await tooLong.ProblemCodeAsync());

        // Un testo più corto solo per Bluesky risolve.
        var withOverride = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts",
            Post(new string('a', 301), DateTime.UtcNow.AddHours(1), [bluesky], overrides: new Dictionary<Guid, string> { [bluesky] = "Corto" }));
        Assert.Equal(HttpStatusCode.Created, withOverride.StatusCode);

        var noImage = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", Post("Ciao", DateTime.UtcNow.AddHours(1), [instagram]));
        var problem = await noImage.ReadJsonAsync();
        Assert.Equal("post_invalid", problem.GetProperty("code").GetString());
        Assert.Contains("immagine", problem.GetProperty("detail").GetString());

        var tooTall = await UploadImageAsync(a, 1000, 2000);
        var ratio = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", Post("Ciao", DateTime.UtcNow.AddHours(1), [instagram], [tooTall]));
        Assert.Contains("proporzioni", (await ratio.ReadJsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Su_Mastodon_si_aspetta_l_immagine_e_il_post_ha_la_chiave_di_idempotenza()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        Assert.Equal(1000, (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/accounts")).ReadJsonAsync())[0]
            .GetProperty("limits").GetProperty("maxCharacters").GetInt32()); // il limite dell'istanza, non 500

        var image = await UploadImageAsync(a);
        Net.On(HttpMethod.Post, $"^{Mastodon}/api/v2/media$", """{"id":"m1","url":null}""", HttpStatusCode.Accepted)
            .On(HttpMethod.Get, $"^{Mastodon}/api/v1/media/m1$", """{"id":"m1","url":"https://files/m1.jpg"}""")
            .On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"id":"s1","url":"https://mastodon.example/@meteo/s1"}""");

        var post = await CreatePostAsync(a, Post("Nuova versione!", DateTime.UtcNow.AddMinutes(-1), [mastodon], [image]));
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Single(Net.Calls(HttpMethod.Get, "/api/v1/media/m1"));
        var status = Net.Calls(HttpMethod.Post, "/api/v1/statuses").Single();
        var fields = QueryHelpers.ParseQuery(status.Body);
        Assert.Equal("Nuova versione!", fields["status"]);
        Assert.Equal("m1", fields["media_ids[]"]);
        var target = Target(await GetPostAsync(a, post), "Mastodon");
        Assert.Contains($"Idempotency-Key: {target.GetProperty("id").GetGuid():N}", status.Headers);
        Assert.Contains("Bearer token-mastodon", status.Headers);
        Assert.Equal("https://mastodon.example/@meteo/s1", target.GetProperty("externalUrl").GetString());
    }

    [Fact]
    public async Task Instagram_scarica_l_immagine_da_un_indirizzo_firmato_che_scade()
    {
        var a = await _app.SignUpAsync();
        var (_, instagram) = await ConnectMetaAsync(a);
        var image = await UploadImageAsync(a, 1080, 1350);
        Net.On(HttpMethod.Post, $"^{Graph}/ig1/media$", """{"id":"c1"}""")
            .On(HttpMethod.Get, $"^{Graph}/c1$", """{"status_code":"FINISHED"}""")
            .On(HttpMethod.Post, $"^{Graph}/ig1/media_publish$", """{"id":"im1"}""")
            .On(HttpMethod.Get, $"^{Graph}/im1$", """{"permalink":"https://www.instagram.com/p/abc/"}""");

        var post = await CreatePostAsync(a, Post("Il nuovo radar #meteo", DateTime.UtcNow.AddMinutes(-1), [instagram], [image]));
        await Worker.RunOnceAsync(CancellationToken.None);

        var container = Net.Calls(HttpMethod.Post, "/ig1/media").Single(c => !c.Url.Contains("publish"));
        var fields = QueryHelpers.ParseQuery(container.Body);
        Assert.Equal("Il nuovo radar #meteo", fields["caption"]);
        Assert.Equal("Il radar sopra l'Italia", fields["alt_text"]);
        Assert.Contains("Bearer token-pagina", container.Headers);
        Assert.Contains("creation_id=c1", Net.Calls(HttpMethod.Post, "media_publish").Single().Body);
        Assert.Equal("https://www.instagram.com/p/abc/", Target(await GetPostAsync(a, post), "Instagram").GetProperty("externalUrl").GetString());

        // L'indirizzo che ha avuto Instagram funziona senza login…
        var imageUrl = new Uri(fields["image_url"]!);
        Assert.Equal("tunnel.example", imageUrl.Host); // Social:PublicUrl, non l'indirizzo del pannello
        var anonymous = _app.CreateClient();
        var served = await anonymous.GetAsync(imageUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(TestJpeg.Create(1080, 1350), await served.Content.ReadAsByteArrayAsync());

        // …ma non con la firma toccata, né per un'altra immagine.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(imageUrl.PathAndQuery[..^2] + "xx")).StatusCode);
        var other = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(imageUrl.PathAndQuery.Replace(imageUrl.Segments[^1][32..64], other))).StatusCode);

        // E un indirizzo scaduto non vale più.
        var signer = _app.Services.GetRequiredService<MediaUrlSigner>();
        var expired = signer.PathFor(a.OrgId, image, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(expired)).StatusCode);
    }

    [Fact]
    public async Task Instagram_senza_Pagina_si_collega_con_Instagram_e_pubblica_su_graph_instagram_com()
    {
        var a = await _app.SignUpAsync();
        var instagram = await ConnectInstagramLoginAsync(a);
        var image = await UploadImageAsync(a);
        Net.On(HttpMethod.Post, $"^{InstagramGraph}/17841/media$", """{"id":"c7"}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/c7$", """{"status_code":"FINISHED"}""")
            .On(HttpMethod.Post, $"^{InstagramGraph}/17841/media_publish$", """{"id":"m7"}""")
            .On(HttpMethod.Get, $"^{InstagramGraph}/m7$", """{"permalink":"https://www.instagram.com/p/xyz/"}""");

        var post = await CreatePostAsync(a, Post("Senza Pagina #meteo", DateTime.UtcNow.AddMinutes(-1), [instagram], [image]));
        await Worker.RunOnceAsync(CancellationToken.None);

        var container = Net.Calls(HttpMethod.Post, "graph.instagram.com/v26.0/17841/media").Single(c => !c.Url.Contains("publish"));
        Assert.Contains("Bearer token-ig", container.Headers);
        Assert.StartsWith("https://tunnel.example/api/v1/social/media/", QueryHelpers.ParseQuery(container.Body)["image_url"].ToString());
        Assert.Empty(Net.Calls(HttpMethod.Post, "graph.facebook.com")); // niente Facebook di mezzo
        Assert.Equal("https://www.instagram.com/p/xyz/", Target(await GetPostAsync(a, post), "Instagram").GetProperty("externalUrl").GetString());
    }

    [Fact]
    public async Task Un_account_Instagram_personale_si_rifiuta()
    {
        var a = await _app.SignUpAsync();
        await ConnectInstagramLoginAsync(a, accountType: "PERSONAL", expected: HttpStatusCode.BadRequest);
        Assert.Equal(0, (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/accounts")).ReadJsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Il_token_di_Instagram_si_rinnova_da_solo_e_uno_scaduto_chiede_di_ricollegare()
    {
        var a = await _app.SignUpAsync();
        var instagram = await ConnectInstagramLoginAsync(a);
        Net.On(HttpMethod.Get, "^https://graph.instagram.com/refresh_access_token$", """{"access_token":"token-rinnovato","token_type":"bearer","expires_in":5183944}""");

        // Un token di un mese fa: ne restano 30 giorni, va rinnovato.
        await WithAccountAsync(a, instagram, x => x.RenewToken(x.ProtectedSecret, DateTime.UtcNow.AddDays(30)));
        await Worker.RunOnceAsync(CancellationToken.None);

        var refresh = Net.Calls(HttpMethod.Get, "refresh_access_token").Single();
        Assert.Contains("grant_type=ig_refresh_token", refresh.Url);
        Assert.Contains("access_token=token-ig", refresh.Url);
        var renewed = await WithAccountAsync(a, instagram, _ => { });
        Assert.True(renewed.TokenExpiresAtUtc > DateTime.UtcNow.AddDays(59));

        // Scaduto: il post non parte e l'account va ricollegato.
        await WithAccountAsync(a, instagram, x => x.RenewToken(x.ProtectedSecret, DateTime.UtcNow.AddMinutes(-1)));
        var post = await CreatePostAsync(a, Post("Ciao", DateTime.UtcNow.AddMinutes(-1), [instagram], [await UploadImageAsync(a)]));
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Contains("scaduto", Target(await GetPostAsync(a, post), "Instagram").GetProperty("error").GetString());
        Assert.Equal(SocialAccountStatus.NeedsReconnect, (await WithAccountAsync(a, instagram, _ => { })).Status);
        Assert.Empty(Net.Calls(HttpMethod.Post, "/17841/media"));
    }

    /// <summary>Legge (e se serve modifica) l'account direttamente nel database, nel tenant giusto.</summary>
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

    [Fact]
    public async Task Su_una_Pagina_Facebook_piu_foto_diventano_un_post_solo()
    {
        var a = await _app.SignUpAsync();
        var (page, _) = await ConnectMetaAsync(a);
        var first = await UploadImageAsync(a);
        var second = await UploadImageAsync(a);
        Net.On(HttpMethod.Post, $"^{Graph}/p1/photos$", """{"id":"ph"}""")
            .On(HttpMethod.Post, $"^{Graph}/p1/feed$", """{"id":"p1_99"}""");

        var post = await CreatePostAsync(a, Post("Due schermate", DateTime.UtcNow.AddMinutes(-1), [page], [first, second]));
        await Worker.RunOnceAsync(CancellationToken.None);

        var photos = Net.Calls(HttpMethod.Post, "/p1/photos").ToList();
        Assert.Equal(2, photos.Count);
        Assert.All(photos, p => Assert.Equal("false", QueryHelpers.ParseQuery(p.Body)["published"]));
        var feed = QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "/p1/feed").Single().Body);
        Assert.Equal("Due schermate", feed["message"]);
        Assert.Equal("{\"media_fbid\":\"ph\"}", feed["attached_media[1]"]);
        Assert.Equal("https://www.facebook.com/p1_99", Target(await GetPostAsync(a, post), "FacebookPage").GetProperty("externalUrl").GetString());
    }

    [Fact]
    public async Task Un_errore_definitivo_lascia_il_motivo_e_si_puo_riprovare()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        Net.On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"error":"Validation failed: Text character limit of 1000 exceeded"}""", (HttpStatusCode)422);

        var post = await CreatePostAsync(a, Post("Ciao", DateTime.UtcNow.AddMinutes(-1), [mastodon]));
        await Worker.RunOnceAsync(CancellationToken.None);

        var failed = await GetPostAsync(a, post);
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Contains("character limit", Target(failed, "Mastodon").GetProperty("error").GetString());
        Assert.True(failed.GetProperty("editable").GetBoolean());

        Net.On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"id":"s2","url":"https://mastodon.example/@meteo/s2"}""");
        var retry = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Published", (await GetPostAsync(a, post)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Un_errore_passeggero_si_riprova_piu_tardi_e_una_password_revocata_chiede_di_ricollegare()
    {
        var a = await _app.SignUpAsync();
        var bluesky = await ConnectBlueskyAsync(a);
        var mastodon = await ConnectMastodonAsync(a);
        Net.On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", "{}", HttpStatusCode.ServiceUnavailable)
            .On(HttpMethod.Post, "^https://bsky.social/xrpc/com.atproto.server.createSession$", """{"error":"AuthenticationRequired","message":"Invalid identifier or password"}""", HttpStatusCode.Unauthorized);

        var post = await CreatePostAsync(a, Post("Ciao", DateTime.UtcNow.AddMinutes(-1), [bluesky, mastodon]));
        await Worker.RunOnceAsync(CancellationToken.None);

        var result = await GetPostAsync(a, post);
        var m = Target(result, "Mastodon");
        Assert.Equal("Pending", m.GetProperty("status").GetString());
        Assert.True(m.GetProperty("nextAttemptAtUtc").GetDateTime() > DateTime.UtcNow);
        Assert.Equal("Failed", Target(result, "Bluesky").GetProperty("status").GetString());

        var accounts = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/accounts")).ReadJsonAsync();
        Assert.Equal("NeedsReconnect", accounts.EnumerateArray().Single(x => x.GetProperty("network").GetString() == "Bluesky").GetProperty("status").GetString());

        // Prima della nuova ora non si ritenta.
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Single(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));
    }

    [Fact]
    public async Task Il_login_Meta_non_vale_per_un_altra_organizzazione()
    {
        var a = await _app.SignUpAsync();
        var b = await _app.SignUpAsync();

        var start = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/meta/start", null)).ReadJsonAsync();
        var state = QueryHelpers.ParseQuery(new Uri(start.GetProperty("url").GetString()!).Query)["state"].ToString();

        var stolen = await b.Client.PostAsJsonAsync($"/api/v1/orgs/{b.OrgId}/social/meta/complete", new { code = "codice", state });
        Assert.Equal("oauth_state", await stolen.ProblemCodeAsync());
        Assert.Empty(Net.Calls(HttpMethod.Get, "oauth/access_token"));
    }

    [Fact]
    public async Task Post_e_account_di_un_altra_organizzazione_non_si_vedono_ne_si_usano()
    {
        var a = await _app.SignUpAsync();
        var b = await _app.SignUpAsync();
        var bluesky = await ConnectBlueskyAsync(a);
        var post = await CreatePostAsync(a, Post("Ciao", DateTime.UtcNow.AddHours(1), [bluesky]));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/orgs/{b.OrgId}/social/posts/{post.GetProperty("id").GetGuid()}")).StatusCode);
        var withForeignAccount = await b.Client.PostAsJsonAsync($"/api/v1/orgs/{b.OrgId}/social/posts", Post("Ciao", DateTime.UtcNow.AddHours(1), [bluesky]));
        Assert.Equal(HttpStatusCode.NotFound, withForeignAccount.StatusCode);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O");
        var to = DateTime.UtcNow.AddDays(10).ToString("O");
        var list = await (await b.Client.GetAsync($"/api/v1/orgs/{b.OrgId}/social/posts?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}")).ReadJsonAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact]
    public async Task Scollegare_un_account_lascia_lo_storico_dei_post_usciti()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        Net.On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"id":"s1","url":"https://mastodon.example/@meteo/s1"}""");
        var published = await CreatePostAsync(a, Post("Uscito", DateTime.UtcNow.AddMinutes(-1), [mastodon]));
        await Worker.RunOnceAsync(CancellationToken.None);
        var pending = await CreatePostAsync(a, Post("Domani", DateTime.UtcNow.AddDays(1), [mastodon]));

        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/{mastodon}")).StatusCode);

        var history = Target(await GetPostAsync(a, published), "Mastodon");
        Assert.Equal("Published", history.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("accountId").ValueKind);
        Assert.Equal(0, (await GetPostAsync(a, pending)).GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task Un_file_che_non_e_un_jpeg_si_rifiuta()
    {
        var a = await _app.SignUpAsync();
        var form = new MultipartFormDataContent { { new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), "file", "foto.png" } };
        var response = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", form);
        Assert.Equal("file_type", await response.ProblemCodeAsync());
    }
}

/// <summary>Un JPEG minimo: solo l'intestazione con le dimensioni, che è quello che il server legge.</summary>
public static class TestJpeg
{
    public static byte[] Create(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9
    ];
}
