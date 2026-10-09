using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Flarelytics.Tests.Integration;

/// <summary>I post ricorrenti: regola, controllo dei limiti, uscite create e pubblicate dal worker, pausa, cancellazione.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class RecurringPostTests(PostgresFixture postgres) : IAsyncLifetime
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
    private FakeStoreServer Net => _app.SocialApis;

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

    private async Task<Guid> UploadImageAsync(Account a)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestJpeg.Create(1080, 1080));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "foto.jpg");
        return (await (await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/social/media", form)).ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private static object Recurring(string text, Guid[] accounts, Guid[]? media = null, string frequency = "Daily", string[]? days = null,
        string time = "09:00", string? end = null, bool paused = false) => new
    {
        text, accountIds = accounts, media = (media ?? []).Select(id => new { id, altText = "Il radar" }).ToArray(),
        frequency, interval = 1, daysOfWeek = days, timeOfDay = time, timeZone = "Europe/Rome",
        startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10)).ToString("yyyy-MM-dd"), endDate = end, isPaused = paused
    };

    private static async Task<JsonElement> CreateAsync(Account a, object body)
    {
        var response = await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/recurring", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<JsonElement> ListAsync(Account a) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/recurring")).ReadJsonAsync();

    private static async Task<List<JsonElement>> CalendarAsync(Account a)
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-5).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(5).ToString("O"));
        return (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/posts?from={from}&to={to}")).ReadJsonAsync()).EnumerateArray().ToList();
    }

    /// <summary>Porta la prossima uscita a <paramref name="atUtc"/>, come se fosse arrivata la sua ora.</summary>
    private async Task MakeDueAsync(Account a, Guid id, DateTime atUtc)
    {
        using var scope = _app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(a.OrgId);
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "SocialRecurringPost" SET "NextOccurrenceUtc" = {atUtc} WHERE "Id" = {id}""");
    }

    [Fact]
    public async Task All_ora_dell_uscita_il_worker_crea_un_post_con_una_copia_dei_file_e_lo_pubblica()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var image = await UploadImageAsync(a);

        var created = await CreateAsync(a, Recurring("Il meteo di oggi", [mastodon], [image]));
        var id = created.GetProperty("id").GetGuid();
        var next = created.GetProperty("nextOccurrenceUtc").GetDateTime();
        Assert.True(next > DateTime.UtcNow);
        Assert.Equal(5, created.GetProperty("upcoming").GetArrayLength());
        Assert.Equal("09:00", created.GetProperty("timeOfDay").GetString());

        // Prima dell'ora non succede niente.
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));

        var due = DateTime.UtcNow.AddMinutes(-2);
        await MakeDueAsync(a, id, due);
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Il meteo di oggi", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Net.Calls(HttpMethod.Post, "/api/v1/statuses").Single().Body)["status"].ToString());
        Assert.Single(Net.Calls(HttpMethod.Post, "/api/v2/media")); // l'immagine della serie, nella sua copia

        var post = Assert.Single(await CalendarAsync(a));
        Assert.Equal(id, post.GetProperty("recurringPostId").GetGuid());
        Assert.Equal("Published", post.GetProperty("status").GetString());
        var copy = post.GetProperty("media")[0];
        Assert.NotEqual(image, copy.GetProperty("id").GetGuid()); // una copia: il file della serie resta per le prossime uscite
        Assert.Equal("Il radar", copy.GetProperty("altText").GetString());

        var series = Assert.Single((await ListAsync(a)).EnumerateArray());
        Assert.Equal(1, series.GetProperty("occurrenceCount").GetInt32());
        Assert.True(series.GetProperty("nextOccurrenceUtc").GetDateTime() > DateTime.UtcNow);
        Assert.Equal("Published", series.GetProperty("lastPost").GetProperty("status").GetString());
        Assert.Equal(image, series.GetProperty("media")[0].GetProperty("id").GetGuid());

        // Il giro dopo non la ripete.
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Single(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));
    }

    [Fact]
    public async Task Un_uscita_mancata_da_ore_si_salta_invece_di_uscire_in_ritardo()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var id = (await CreateAsync(a, Recurring("Buongiorno", [mastodon]))).GetProperty("id").GetGuid();

        await MakeDueAsync(a, id, DateTime.UtcNow.AddHours(-5));
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));
        Assert.Empty(await CalendarAsync(a));
        var series = Assert.Single((await ListAsync(a)).EnumerateArray());
        Assert.Equal(0, series.GetProperty("occurrenceCount").GetInt32());
        Assert.True(series.GetProperty("nextOccurrenceUtc").GetDateTime() > DateTime.UtcNow);
    }

    [Fact]
    public async Task In_pausa_non_esce_e_riprendendo_si_riparte_dalla_prossima()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var id = (await CreateAsync(a, Recurring("Buongiorno", [mastodon]))).GetProperty("id").GetGuid();

        var paused = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/recurring/{id}/paused", new { paused = true })).ReadJsonAsync();
        Assert.True(paused.GetProperty("isPaused").GetBoolean());
        Assert.Equal(0, paused.GetProperty("upcoming").GetArrayLength());

        await MakeDueAsync(a, id, DateTime.UtcNow.AddMinutes(-1));
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));

        var resumed = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/recurring/{id}/paused", new { paused = false })).ReadJsonAsync();
        Assert.True(resumed.GetProperty("nextOccurrenceUtc").GetDateTime() > DateTime.UtcNow);
        await Worker.RunOnceAsync(CancellationToken.None);
        Assert.Empty(Net.Calls(HttpMethod.Post, "/api/v1/statuses"));
    }

    [Fact]
    public async Task Si_controllano_limiti_delle_reti_e_regola_quando_si_salva()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var path = $"/api/v1/orgs/{a.OrgId}/social/recurring";

        Assert.Equal("post_invalid", await (await a.Client.PostAsJsonAsync(path, Recurring(new string('a', 501), [mastodon]))).ProblemCodeAsync());
        Assert.Equal("no_accounts", await (await a.Client.PostAsJsonAsync(path, Recurring("Ciao", []))).ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PostAsJsonAsync(path, Recurring("Ciao", [mastodon], frequency: "Weekly", days: []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PostAsJsonAsync(path, Recurring("Ciao", [mastodon], time: "25:00"))).StatusCode);
        var ended = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd");
        Assert.Equal("recurrence_ended", await (await a.Client.PostAsJsonAsync(path, Recurring("Ciao", [mastodon], end: ended))).ProblemCodeAsync());

        var weekly = await CreateAsync(a, Recurring("Il lunedì", [mastodon], frequency: "Weekly", days: ["Monday"]));
        Assert.All(weekly.GetProperty("upcoming").EnumerateArray(), d =>
            Assert.Equal(DayOfWeek.Monday, TimeZoneInfo.ConvertTimeFromUtc(d.GetDateTime().ToUniversalTime(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")).DayOfWeek));
    }

    [Fact]
    public async Task Il_calendario_riceve_le_uscite_future_calcolate_dalla_regola()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var id = (await CreateAsync(a, Recurring("Ogni giorno", [mastodon]))).GetProperty("id").GetGuid();
        await CreateAsync(a, Recurring("In pausa", [mastodon], paused: true));

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-3).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddDays(7).ToString("O"));
        var occurrences = (await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/social/recurring/occurrences?from={from}&to={to}")).ReadJsonAsync())
            .EnumerateArray().ToList();

        // Solo future, solo della serie attiva: una al giorno per 7 giorni.
        Assert.InRange(occurrences.Count, 6, 7);
        Assert.All(occurrences, o =>
        {
            Assert.Equal(id, o.GetProperty("recurringPostId").GetGuid());
            Assert.True(o.GetProperty("atUtc").GetDateTime() > DateTime.UtcNow);
        });
    }

    [Fact]
    public async Task Cancellare_la_serie_lascia_le_uscite_fatte_e_scollegare_un_account_lo_toglie_dalla_serie()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var image = await UploadImageAsync(a);
        var id = (await CreateAsync(a, Recurring("Il meteo", [mastodon], [image]))).GetProperty("id").GetGuid();
        await MakeDueAsync(a, id, DateTime.UtcNow.AddMinutes(-1));
        await Worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/accounts/{mastodon}")).StatusCode);
        Assert.Equal(0, (await ListAsync(a))[0].GetProperty("accountIds").GetArrayLength());

        var storage = _app.Services.GetRequiredService<SocialMediaStorage>();
        Assert.True(File.Exists(storage.PathFor(a.OrgId, image, ".jpg")));
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/recurring/{id}")).StatusCode);
        Assert.False(File.Exists(storage.PathFor(a.OrgId, image, ".jpg")));

        var post = Assert.Single(await CalendarAsync(a));
        Assert.Equal(JsonValueKind.Null, post.GetProperty("recurringPostId").ValueKind);
        var copy = post.GetProperty("media")[0].GetProperty("id").GetGuid();
        Assert.True(File.Exists(storage.PathFor(a.OrgId, copy, ".jpg"))); // la copia dell'uscita resta
    }

    [Fact]
    public async Task Duplicare_copia_le_immagini_e_la_copia_resta_indipendente_dall_originale()
    {
        var a = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var image = await UploadImageAsync(a);
        var original = await CreateAsync(a, Recurring("Il meteo del lunedì", [mastodon], [image], frequency: "Weekly", days: ["Monday"]));

        // Come fa il pannello: copie libere delle immagini, poi un post nuovo con il testo cambiato.
        var copies = await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/media/copies", new { ids = new[] { image } })).ReadJsonAsync();
        var copyId = copies[0].GetProperty("id").GetGuid();
        Assert.NotEqual(image, copyId);
        Assert.Equal("Il radar", copies[0].GetProperty("altText").GetString());

        // L'immagine dell'originale non si può prendere: appartiene già a un post ricorrente.
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/social/recurring",
            Recurring("Il meteo del giovedì", [mastodon], [image], frequency: "Weekly", days: ["Thursday"]))).StatusCode);
        var duplicate = await CreateAsync(a, Recurring("Il meteo del giovedì", [mastodon], [copyId], frequency: "Weekly", days: ["Thursday"]));

        var list = (await ListAsync(a)).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(image, list.Single(r => r.GetProperty("id").GetGuid() == original.GetProperty("id").GetGuid()).GetProperty("media")[0].GetProperty("id").GetGuid());

        // Cancellare la copia non tocca il file dell'originale.
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/orgs/{a.OrgId}/social/recurring/{duplicate.GetProperty("id").GetGuid()}")).StatusCode);
        var storage = _app.Services.GetRequiredService<SocialMediaStorage>();
        Assert.True(File.Exists(storage.PathFor(a.OrgId, image, ".jpg")));
        Assert.False(File.Exists(storage.PathFor(a.OrgId, copyId, ".jpg")));
    }

    [Fact]
    public async Task Un_altra_organizzazione_non_vede_ne_tocca_i_post_ricorrenti()
    {
        var a = await _app.SignUpAsync();
        var b = await _app.SignUpAsync();
        var mastodon = await ConnectMastodonAsync(a);
        var id = (await CreateAsync(a, Recurring("Solo nostro", [mastodon]))).GetProperty("id").GetGuid();

        Assert.Equal(0, (await ListAsync(b)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync($"/api/v1/orgs/{b.OrgId}/social/recurring/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/v1/orgs/{b.OrgId}/social/recurring", Recurring("Rubato", [mastodon]))).StatusCode);
    }
}
