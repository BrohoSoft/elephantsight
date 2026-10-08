using System.Net;
using System.Net.Http.Json;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Sync;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Flarelytics.Tests.Integration.ReleaseTests;

namespace Flarelytics.Tests.Integration;

/// <summary>Recensioni dei due store in una lista sola, con le risposte che partono verso lo store.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class ReviewTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;
    private SyncHost _sync = null!;

    public async Task InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(connection);
        _ = _app.Services;
        _sync = new SyncHost(_app, connection, backfillDays: 1);

        await using var scope = _sync.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Flarelytics.Core.Database.FlarelyticsDbContext>();
        db.Add(new ExchangeRate { Date = DateOnly.FromDateTime(DateTime.UtcNow), Currency = "USD", UnitsPerEuro = 1.1m });
        await db.SaveChangesAsync();

        _app.StoreApis
            .On(HttpMethod.Get, $"/v1/apps/{AppleId}/customerReviews$", """
                {"data":[
                  {"type":"customerReviews","id":"r-apple-1","attributes":{"rating":2,"title":"Si blocca","body":"Crasha all'avvio","reviewerNickname":"luca","createdDate":"2026-10-05T08:00:00Z","territory":"ITA"},
                   "relationships":{"response":{"data":null}}},
                  {"type":"customerReviews","id":"r-apple-2","attributes":{"rating":5,"title":"Ottima","body":"Perfetta","reviewerNickname":"anna","createdDate":"2026-10-01T08:00:00Z","territory":"ITA"},
                   "relationships":{"response":{"data":{"type":"customerReviewResponses","id":"resp-1"}}}}
                ],
                 "included":[{"type":"customerReviewResponses","id":"resp-1","attributes":{"responseBody":"Grazie!","lastModifiedDate":"2026-10-02T08:00:00Z","state":"PUBLISHED"}}]}
                """)
            .On(HttpMethod.Get, $"/applications/{Package}/reviews$", """
                {"reviews":[{"reviewId":"g-1","authorName":"Marco","comments":[
                   {"userComment":{"text":"Non sincronizza","starRating":1,"reviewerLanguage":"it","appVersionName":"1.9","lastModified":{"seconds":"1791360000"}}}]}]}
                """);
    }

    public async Task DisposeAsync()
    {
        await _sync.DisposeAsync();
        await _app.DisposeAsync();
    }

    private Task SyncAsync() => _sync.Services.GetRequiredService<SyncCoordinator>().RunOnceAsync(CancellationToken.None);

    [Fact]
    public async Task Le_recensioni_dei_due_store_in_una_lista_dalla_piu_recente()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        await SyncAsync();

        var page = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/reviews")).ReadJsonAsync();
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(["g-1-Non sincronizza", "Crasha all'avvio", "Perfetta"],
            items.Select(i => (i.GetProperty("store").GetString() == "GooglePlay" ? "g-1-" : "") + i.GetProperty("body").GetString()));
        Assert.Equal("Meteo", items[0].GetProperty("projectName").GetString());
        Assert.Equal("Grazie!", items[2].GetProperty("replyText").GetString());

        var apple = page.GetProperty("summary").EnumerateArray().Single(x => x.GetProperty("store").GetString() == "AppStore");
        Assert.Equal(3.5, apple.GetProperty("average").GetDouble());

        var unanswered = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/reviews?unanswered=true&projectId={project}")).ReadJsonAsync();
        Assert.Equal(2, unanswered.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task La_risposta_parte_verso_lo_store_giusto()
    {
        var (account, _) = await ProjectWithBothAppsAsync(_app);
        await SyncAsync();
        _app.StoreApis
            .On(HttpMethod.Post, "/v1/customerReviewResponses$", """{"data":{"type":"customerReviewResponses","id":"resp-2","attributes":{"responseBody":"x","state":"PENDING_PUBLISH"}}}""")
            .On(HttpMethod.Delete, "/v1/customerReviewResponses/resp-1$", "")
            .On(HttpMethod.Post, $"/applications/{Package}/reviews/g-1:reply$", """{"result":{"replyText":"x"}}""");

        var items = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/reviews")).ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        Guid Id(string body) => items.Single(i => i.GetProperty("body").GetString() == body).GetProperty("id").GetGuid();

        var google = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/reviews/{Id("Non sincronizza")}/reply", new { text = "Ci stiamo lavorando" });
        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        Assert.Contains("Ci stiamo lavorando", _app.StoreApis.Calls(HttpMethod.Post, "g-1:reply").Single().Body);

        // Apple: la risposta che c'era si sostituisce.
        var apple = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/reviews/{Id("Perfetta")}/reply", new { text = "Grazie mille" });
        Assert.Equal("PENDING_PUBLISH", (await apple.ReadJsonAsync()).GetProperty("replyState").GetString());
        Assert.Single(_app.StoreApis.Calls(HttpMethod.Delete, "customerReviewResponses/resp-1"));
        var body = _app.StoreApis.Calls(HttpMethod.Post, "/v1/customerReviewResponses").Single().Body!;
        Assert.Contains("\"r-apple-2\"", body);
        Assert.Contains("Grazie mille", body);
    }

    [Fact]
    public async Task Su_google_play_le_risposte_lunghe_si_rifiutano_prima_di_mandarle()
    {
        var (account, _) = await ProjectWithBothAppsAsync(_app);
        await SyncAsync();
        var google = (await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/reviews?store=GooglePlay")).ReadJsonAsync())
            .GetProperty("items")[0].GetProperty("id").GetGuid();

        var response = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/reviews/{google}/reply", new { text = new string('a', 351) });

        Assert.Equal("reply_too_long", await response.ProblemCodeAsync());
        Assert.Empty(_app.StoreApis.Calls(HttpMethod.Post, ":reply"));
    }

    [Fact]
    public async Task Una_chiave_senza_permesso_per_le_recensioni_non_ferma_il_resto()
    {
        var (account, _) = await ProjectWithBothAppsAsync(_app);
        _app.StoreApis.On(HttpMethod.Get, $"/v1/apps/{AppleId}/customerReviews$", """{"errors":[{"detail":"forbidden"}]}""", HttpStatusCode.Forbidden);

        await SyncAsync();

        var page = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/reviews")).ReadJsonAsync();
        Assert.Equal(1, page.GetProperty("total").GetInt32()); // quella di Google c'è
        var appleSync = page.GetProperty("sync").EnumerateArray().Single(x => x.GetProperty("store").GetString() == "AppStore");
        Assert.Contains("permesso", appleSync.GetProperty("lastError").GetString());
    }
}
