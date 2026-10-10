using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Core.Social.Media;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// I file dei post su Bunny (cifrati) o sul disco: caricamento, lettura dalla
/// rotta firmata (anche a intervalli), pubblicazione, copie, pulizia dopo la
/// pubblicazione, modalità remota imposta senza Bunny, cambio di storage.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class MediaStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Mastodon = "https://mastodon.example";

    private string _connection = null!;
    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;

    public async Task InitializeAsync() => _connection = await postgres.CreateDatabaseAsync();

    /// <summary>L'API e il worker con la configurazione del test (Bunny sì o no, modalità).</summary>
    private void Start(Dictionary<string, string?>? settings = null)
    {
        _app = new FlarelyticsAppFactory(_connection, settings);
        _ = _app.Services;
        _sync = new SyncHost(_app, _connection, backfillDays: 1);
    }

    private static Dictionary<string, string?> WithBunny(string mode = "Auto") =>
        new(FakeBunny.Settings()) { ["Media:Storage"] = mode };

    public async Task DisposeAsync()
    {
        if (_sync is not null) await _sync.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    private FakeBunny Bunny => _app.Bunny;
    private FakeStoreServer Net => _app.SocialApis;
    private SocialPublishWorker Worker => _sync.Services.GetServices<IHostedService>().OfType<SocialPublishWorker>().Single();
    private string LocalRoot => _app.Services.GetRequiredService<LocalMediaBackend>().Root;

    /// <summary>Un JPEG riconoscibile: l'intestazione che il server legge, poi byte casuali (che non devono comparire su Bunny).</summary>
    private static byte[] Jpeg(int size = 300_000) => [.. TestJpeg.Create(1080, 1080)[..^2], .. RandomNumberGenerator.GetBytes(size), 0xFF, 0xD9];

    private static async Task<JsonElement> UploadAsync(Account a, byte[] file, string name = "foto.jpg", string type = "image/jpeg")
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        var response = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", new MultipartFormDataContent { { content, "file", name } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private async Task<Guid> ConnectMastodonAsync(Account a)
    {
        Net.On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/verify_credentials$", """{"id":"42","username":"meteo","display_name":"Meteo"}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v2/instance$", """{"configuration":{"statuses":{"max_characters":500}}}""")
            .On(HttpMethod.Get, $"^{Mastodon}/api/v1/accounts/42/statuses$", "[]")
            .On(HttpMethod.Post, $"^{Mastodon}/api/v2/media$", """{"id":"m1","url":"https://mastodon.example/m1.jpg"}""")
            .On(HttpMethod.Post, $"^{Mastodon}/api/v1/statuses$", """{"id":"s1","url":"https://mastodon.example/@meteo/s1"}""");
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/mastodon",
            new { instanceUrl = "mastodon.example", accessToken = "token-mastodon" });
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ScheduleAsync(Account a, Guid account, params Guid[] media)
    {
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/posts", new
        {
            text = "Il meteo di oggi", scheduledAtUtc = DateTime.UtcNow.AddMinutes(-1), isDraft = false, projectId = (Guid?)null,
            accountIds = new[] { account }, media = media.Select(id => new { id, altText = (string?)null }), overrides = (object?)null
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetPostAsync(Account a, Guid postId) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{postId}")).ReadJsonAsync();

    private async Task<Account> InstanceAdminAsync()
    {
        var a = await _app.SignUpAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        (await db.Set<User>().SingleAsync(u => u.Email == a.Email)).SetInstanceAdmin(true);
        await db.SaveChangesAsync();
        return a;
    }

    /// <summary>La pulizia dell'ora, come la fa il worker, con il post uscito <paramref name="daysAgo"/> giorni fa.</summary>
    private async Task CleanupAsync(Account a, Guid postId, int daysAgo)
    {
        await using var scope = _sync.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "SocialPostTarget" SET "PublishedAtUtc" = {DateTime.UtcNow.AddDays(-daysAgo)} WHERE "PostId" = {postId} AND "PublishedAtUtc" IS NOT NULL""");
        await Worker.CleanupPublishedMediaAsync(db, scope.ServiceProvider.GetRequiredService<SocialMediaStore>(),
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<MediaStorageOptions>>().CurrentValue, CancellationToken.None);
    }

    private string BunnyPath(Account a, Guid media, bool thumbnail = false) => $"social/{a.OrgId:N}/{media:N}{(thumbnail ? ".thumb" : "")}.bin";

    [Fact]
    public async Task Con_Bunny_il_file_arriva_cifrato_e_la_rotta_firmata_lo_restituisce_in_chiaro()
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var image = Jpeg();

        var media = await UploadAsync(a, image);
        var id = media.GetProperty("id").GetGuid();

        // Su Bunny un file solo, con il contenuto cifrato; sul disco niente.
        var stored = Assert.Single(Bunny.Files);
        Assert.StartsWith(BunnyPath(a, id), stored.Key);
        Assert.Equal(MediaCipher.EncryptedLength(image.Length, MediaCipher.DefaultBlockSize), stored.Value.Length);
        Assert.True(stored.Value.AsSpan().IndexOf(image.AsSpan(1000, 64)) < 0);
        Assert.False(Directory.Exists(LocalRoot) && Directory.EnumerateFiles(LocalRoot, "*", SearchOption.AllDirectories).Any());

        // La rotta pubblica con la firma lo decifra; senza firma valida, niente.
        var url = media.GetProperty("url").GetString()!;
        var served = await a.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/jpeg", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal(image, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(url.Replace("&s=", "&s=x"))).StatusCode);
        var expired = System.Text.RegularExpressions.Regex.Replace(url, @"e=\d+", "e=1000");
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(expired)).StatusCode);
        // Nessun indirizzo di Bunny esce verso il pannello.
        Assert.DoesNotContain("bunnycdn", media.GetRawText());

        // La miniatura: cifrata anche lei, accanto all'originale.
        var thumb = TestJpeg.Create(400, 400);
        var withThumb = await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{id}/thumbnail", new ByteArrayContent(thumb))).ReadJsonAsync();
        Assert.True(Bunny.Files.ContainsKey(BunnyPath(a, id, thumbnail: true)));
        var thumbUrl = withThumb.GetProperty("thumbnailUrl").GetString()!;
        Assert.Equal(thumb, await (await a.Client.GetAsync(thumbUrl)).Content.ReadAsByteArrayAsync());
        // La firma della miniatura non apre l'originale, e viceversa.
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync(thumbUrl.Replace(".thumb.jpg", ".jpg"))).StatusCode);

        // Non è un JPEG: rifiutata.
        Assert.Equal("file_type", await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{id}/thumbnail",
            new ByteArrayContent(new byte[100]))).ProblemCodeAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_video_su_Bunny_si_legge_a_intervalli_scaricando_solo_i_blocchi_che_servono(bool bunnyIgnoresRange)
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var video = TestVideo.Create(1080, 1920, 15_000, padding: 3_500_000);
        RandomNumberGenerator.Fill(video.AsSpan(video.Length - 3_400_000, 3_000_000));
        var url = (await UploadAsync(a, video, "reel.mp4", "video/mp4")).GetProperty("url").GetString()!;
        Bunny.IgnoreRange = bunnyIgnoresRange;
        Bunny.Requests.Clear();

        async Task<HttpResponseMessage> RangeAsync(string range)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Range", range);
            return await a.Client.SendAsync(request);
        }

        // In mezzo, a cavallo di due blocchi da 1 MiB.
        var middle = await RangeAsync("bytes=1500000-2200000");
        Assert.Equal(HttpStatusCode.PartialContent, middle.StatusCode);
        Assert.Equal("video/mp4", middle.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"bytes 1500000-2200000/{video.Length}", middle.Content.Headers.ContentRange!.ToString());
        Assert.Equal(video[1500000..2200001], await middle.Content.ReadAsByteArrayAsync());
        if (!bunnyIgnoresRange)
        {
            // L'intestazione, poi solo i blocchi 1 e 2.
            var ranges = Bunny.Requests.Select(r => r.Range).ToList();
            Assert.Equal(2, ranges.Count);
            Assert.Equal($"bytes=0-{MediaCipher.HeaderSize - 1}", ranges[0]);
            Assert.StartsWith($"bytes={MediaCipher.HeaderSize + MediaCipher.DefaultBlockSize + MediaCipher.TagSize}-", ranges[1]);
        }

        // Gli ultimi 100 byte, e dall'offset alla fine.
        Assert.Equal(video[^100..], await (await RangeAsync("bytes=-100")).Content.ReadAsByteArrayAsync());
        Assert.Equal(video[3_000_000..], await (await RangeAsync("bytes=3000000-")).Content.ReadAsByteArrayAsync());

        // Oltre la fine: 416, con la lunghezza.
        var outside = await RangeAsync($"bytes={video.Length + 10}-");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, outside.StatusCode);
        Assert.Equal($"bytes */{video.Length}", outside.Content.Headers.ContentRange!.ToString());

        // Senza Range: tutto, e il server dice che gli intervalli si possono chiedere.
        var all = await a.Client.GetAsync(url);
        Assert.Equal(video, await all.Content.ReadAsByteArrayAsync());
        Assert.Contains("bytes", all.Headers.AcceptRanges);
    }

    [Fact]
    public async Task Il_worker_pubblica_leggendo_da_Bunny_e_la_pulizia_cancella_l_originale_dopo_i_giorni_previsti()
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var image = Jpeg();
        var id = (await UploadAsync(a, image)).GetProperty("id").GetGuid();
        await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{id}/thumbnail", new ByteArrayContent(TestJpeg.Create(400, 400)));
        var postId = await ScheduleAsync(a, mastodon, id);

        await Worker.RunOnceAsync(CancellationToken.None);

        // Mastodon ha ricevuto l'immagine in chiaro, decifrata dal server.
        var upload = Assert.Single(Net.Calls(HttpMethod.Post, "/api/v2/media"));
        Assert.True(upload.Bytes!.AsSpan().IndexOf(image.AsSpan(1000, 64)) >= 0);
        Assert.Equal("Published", (await GetPostAsync(a, postId)).GetProperty("status").GetString());

        // Uscito da 3 giorni: troppo presto, l'originale resta.
        await CleanupAsync(a, postId, daysAgo: 3);
        Assert.True(Bunny.Files.ContainsKey(BunnyPath(a, id)));

        // Da 8: l'originale va via, la miniatura resta e il pannello lo sa.
        await CleanupAsync(a, postId, daysAgo: 8);
        Assert.False(Bunny.Files.ContainsKey(BunnyPath(a, id)));
        Assert.True(Bunny.Files.ContainsKey(BunnyPath(a, id, thumbnail: true)));
        var media = (await GetPostAsync(a, postId)).GetProperty("media")[0];
        Assert.True(media.GetProperty("originalDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, media.GetProperty("url").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync(media.GetProperty("thumbnailUrl").GetString())).StatusCode);

        // Duplicare un post così non si può: lo dice invece di copiare il nulla.
        Assert.Equal("media_original_deleted", await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/copies",
            new { ids = new[] { id } })).ProblemCodeAsync());

        // Cancellando il post se ne va anche la miniatura.
        await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{postId}");
        Assert.Empty(Bunny.Files);
    }

    [Fact]
    public async Task La_pulizia_lascia_gli_originali_senza_miniatura()
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var withThumb = (await UploadAsync(a, Jpeg())).GetProperty("id").GetGuid();
        await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{withThumb}/thumbnail", new ByteArrayContent(TestJpeg.Create(400, 400)));
        // Come un file caricato prima delle miniature, o arrivato dall'API pubblica e mai aperto nel pannello.
        var without = (await UploadAsync(a, Jpeg())).GetProperty("id").GetGuid();
        var postId = await ScheduleAsync(a, mastodon, withThumb, without);
        await Worker.RunOnceAsync(CancellationToken.None);

        await CleanupAsync(a, postId, daysAgo: 30);
        Assert.False(Bunny.Files.ContainsKey(BunnyPath(a, withThumb)));
        Assert.True(Bunny.Files.ContainsKey(BunnyPath(a, without)));
        var media = (await GetPostAsync(a, postId)).GetProperty("media");
        Assert.True(media[0].GetProperty("originalDeleted").GetBoolean());
        Assert.False(media[1].GetProperty("originalDeleted").GetBoolean());

        // Quando il pannello gli fa la miniatura, al giro dopo va anche lui.
        await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{without}/thumbnail", new ByteArrayContent(TestJpeg.Create(400, 400)));
        await CleanupAsync(a, postId, daysAgo: 30);
        Assert.False(Bunny.Files.ContainsKey(BunnyPath(a, without)));
    }

    [Fact]
    public async Task La_pulizia_lascia_i_file_dei_post_con_un_account_non_riuscito()
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var id = (await UploadAsync(a, Jpeg())).GetProperty("id").GetGuid();
        await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media/{id}/thumbnail", new ByteArrayContent(TestJpeg.Create(400, 400)));
        var postId = await ScheduleAsync(a, mastodon, id);
        await Worker.RunOnceAsync(CancellationToken.None);

        // Un secondo account, fallito.
        await using (var scope = _sync.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SocialPostTarget" ("Id", "TenantId", "PostId", "AccountId", "Network", "AccountName", "Status", "Attempts", "Error", "CreatedAtUtc")
                VALUES ({Guid.NewGuid()}, {a.OrgId}, {postId}, NULL, {(int)SocialNetwork.Bluesky}, 'meteo.bsky.social', {(int)SocialTargetStatus.Failed}, 3, 'Errore della rete', {DateTime.UtcNow})
                """);
        }

        await CleanupAsync(a, postId, daysAgo: 30);
        Assert.True(Bunny.Files.ContainsKey(BunnyPath(a, id)));
        Assert.False((await GetPostAsync(a, postId)).GetProperty("media")[0].GetProperty("originalDeleted").GetBoolean());
    }

    [Fact]
    public async Task Modalità_remota_senza_Bunny_rifiuta_i_file_ma_non_i_post_di_solo_testo()
    {
        Start(new Dictionary<string, string?> { ["Media:Storage"] = "remote" });
        var a = await _app.SignUpAsync();

        var instance = await (await _app.CreateClient().GetAsync("/api/v1/instance")).ReadJsonAsync();
        Assert.Equal("unavailable", instance.GetProperty("mediaStorage").GetString());
        Assert.True(instance.GetProperty("mediaStorageForced").GetBoolean());

        var content = new ByteArrayContent(Jpeg());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var refused = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", new MultipartFormDataContent { { content, "file", "foto.jpg" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("media_storage_unavailable", await refused.ProblemCodeAsync());
        Assert.Equal("media_storage_unavailable", await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads",
            new { fileName = "reel.mp4", size = 1000 })).ProblemCodeAsync());

        // Niente è rimasto sul disco.
        Assert.False(Directory.Exists(LocalRoot) && Directory.EnumerateFiles(LocalRoot, "*", SearchOption.AllDirectories).Any());

        // Un post di solo testo esce lo stesso.
        var mastodon = await ConnectMastodonAsync(a);
        var postId = await ScheduleAsync(a, mastodon);
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Published", (await GetPostAsync(a, postId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cambiando_storage_i_file_vecchi_restano_leggibili_e_cancellabili_dove_sono()
    {
        Start();
        var admin = await InstanceAdminAsync();
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);

        Assert.Equal("local", (await (await _app.CreateClient().GetAsync("/api/v1/instance")).ReadJsonAsync()).GetProperty("mediaStorage").GetString());
        var oldImage = Jpeg(1000);
        var old = await UploadAsync(a, oldImage);
        Assert.Single(Directory.EnumerateFiles(LocalRoot, "*.jpg", SearchOption.AllDirectories));

        // Bunny dal pannello: vale subito, e la password non torna indietro.
        var saved = await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/bunny",
            new { storageZone = FakeBunny.Zone, region = "", accessKey = FakeBunny.Password });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(FakeBunny.Password, await saved.Content.ReadAsStringAsync());
        Assert.DoesNotContain(FakeBunny.Password, await (await admin.Client.GetAsync("/api/v1/instance/settings")).Content.ReadAsStringAsync());
        Assert.Equal("remote", (await (await _app.CreateClient().GetAsync("/api/v1/instance")).ReadJsonAsync()).GetProperty("mediaStorage").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.PostAsync("/api/v1/instance/settings/bunny/test", null)).StatusCode);
        Assert.Empty(Bunny.Files); // il file di prova non resta
        Assert.Equal("bunny_region", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/bunny", new { region = "marte" })).ProblemCodeAsync());

        var newImage = Jpeg(1000);
        var fresh = await UploadAsync(a, newImage);
        Assert.Single(Bunny.Files);
        Assert.Single(Directory.EnumerateFiles(LocalRoot, "*.jpg", SearchOption.AllDirectories));

        // Tutti e due si leggono, ciascuno dal suo storage.
        Assert.Equal(oldImage, await (await a.Client.GetAsync(old.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());
        Assert.Equal(newImage, await (await a.Client.GetAsync(fresh.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());

        // Duplicare il vecchio: la copia va nello storage di adesso.
        var copy = (await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/copies",
            new { ids = new[] { old.GetProperty("id").GetGuid() } })).ReadJsonAsync())[0];
        Assert.Equal(2, Bunny.Files.Count);
        Assert.Equal(oldImage, await (await a.Client.GetAsync(copy.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());

        // Un post con tutti e tre esce, e cancellandolo spariscono da tutte e due le parti. Il worker
        // dei test è un processo a parte: gli si dà Bunny come l'API l'ha preso dalle impostazioni.
        await _sync.DisposeAsync();
        _sync = new SyncHost(_app, _connection, backfillDays: 1, FakeBunny.Settings());
        var postId = await ScheduleAsync(a, mastodon, old.GetProperty("id").GetGuid(), fresh.GetProperty("id").GetGuid(), copy.GetProperty("id").GetGuid());
        await Worker.RunOnceAsync(CancellationToken.None);
        var published = await GetPostAsync(a, postId);
        Assert.True(published.GetProperty("status").GetString() == "Published", published.GetRawText());
        Assert.Equal(3, Net.Calls(HttpMethod.Post, "/api/v2/media").Count());
        await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/posts/{postId}");
        Assert.Empty(Bunny.Files);
        Assert.Empty(Directory.EnumerateFiles(LocalRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Una_copia_su_Bunny_ha_una_cifratura_sua_e_un_file_spostato_non_si_decifra()
    {
        Start(WithBunny());
        var a = await _app.SignUpAsync();
        var image = Jpeg();
        var original = await UploadAsync(a, image);
        var copy = (await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/copies",
            new { ids = new[] { original.GetProperty("id").GetGuid() } })).ReadJsonAsync())[0];

        var source = Bunny.Files[BunnyPath(a, original.GetProperty("id").GetGuid())];
        var copied = Bunny.Files[BunnyPath(a, copy.GetProperty("id").GetGuid())];
        Assert.NotEqual(source, copied);
        Assert.Equal(image, await (await a.Client.GetAsync(copy.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());

        // Il file dell'originale messo al posto della copia: i dati associati non tornano, e non esce niente in chiaro.
        Bunny.Files[BunnyPath(a, copy.GetProperty("id").GetGuid())] = source;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var response = await a.Client.GetAsync(copy.GetProperty("url").GetString());
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();
        });

        // Lo stesso file in un'altra organizzazione.
        var b = await _app.SignUpAsync();
        var other = await UploadAsync(b, Jpeg());
        Bunny.Files[BunnyPath(b, other.GetProperty("id").GetGuid())] = source;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var response = await b.Client.GetAsync(other.GetProperty("url").GetString());
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync();
        });
    }

    [Fact]
    public async Task In_modalità_remota_un_video_a_pezzi_non_lascia_file_sul_disco_e_i_temporanei_abbandonati_si_cancellano()
    {
        Start(WithBunny("Remote"));
        var a = await _app.SignUpAsync();
        var video = TestVideo.Create(1080, 1920, 15_000, padding: 2_000_000);
        var tempRoot = _app.Services.CreateScope().ServiceProvider.GetRequiredService<SocialMediaStore>().TempRoot;

        var start = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads", new { fileName = "reel.mp4", size = video.Length })).ReadJsonAsync();
        var uploadId = start.GetProperty("uploadId").GetGuid();
        for (var from = 0; from < video.Length; from += 1_000_000)
        {
            var length = Math.Min(1_000_000, video.Length - from);
            Assert.Equal(HttpStatusCode.OK, (await a.Client.PutAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{uploadId}?offset={from}",
                new ByteArrayContent(video[from..(from + length)]))).StatusCode);
        }
        var done = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads/{uploadId}/complete", new { fileName = "reel.mp4" })).ReadJsonAsync();

        Assert.Single(Bunny.Files);
        Assert.Equal(video, await (await a.Client.GetAsync(done.GetProperty("url").GetString())).Content.ReadAsByteArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(LocalRoot) && Directory.EnumerateFiles(LocalRoot, "*", SearchOption.AllDirectories).Any());

        // Un caricamento lasciato a metà: il worker lo toglie dopo un giorno.
        var abandoned = (await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/uploads", new { fileName = "x.mp4", size = 10 })).ReadJsonAsync())
            .GetProperty("uploadId").GetGuid();
        var part = Assert.Single(Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories));
        Assert.Contains(abandoned.ToString("N"), part);
        File.SetLastWriteTimeUtc(part, DateTime.UtcNow.AddDays(-2));
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Gli_errori_di_Bunny_non_contengono_la_password()
    {
        var wrong = new Dictionary<string, string?>(FakeBunny.Settings()) { ["Media:Bunny:AccessKey"] = "password-sbagliata-123456" };
        Start(wrong);
        var a = await _app.SignUpAsync();

        var content = new ByteArrayContent(Jpeg());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var refused = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", new MultipartFormDataContent { { content, "file", "foto.jpg" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("media_storage_unavailable", body);
        Assert.DoesNotContain("password-sbagliata-123456", body);

        Bunny.Down = true;
        var down = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", new MultipartFormDataContent { { content, "file", "foto.jpg" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.DoesNotContain("password-sbagliata-123456", await down.Content.ReadAsStringAsync());

        // Nemmeno nei log del pannello.
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        await Task.Delay(1500); // i log si scrivono a blocchi
        Assert.False(await db.Set<Flarelytics.Core.Logging.LogEntry>().AnyAsync(l => l.Message.Contains("password-sbagliata-123456") || (l.Exception != null && l.Exception.Contains("password-sbagliata-123456"))));
    }
}
