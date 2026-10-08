using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Flarelytics.Tests.Integration.Infrastructure;
using static Flarelytics.Tests.Integration.ReleaseTests;

namespace Flarelytics.Tests.Integration;

/// <summary>La pagina dello store: testi e screenshot, letti e modificati dal pannello.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class ListingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Apple con la versione pubblicata (non modificabile) o in preparazione.</summary>
    private void Apple(string versionState)
    {
        _app.StoreApis
            .On(HttpMethod.Get, $"/v1/apps/{AppleId}/appInfos$", """{"data":[{"type":"appInfos","id":"info-1","attributes":{"state":"READY_FOR_DISTRIBUTION"}}]}""")
            .On(HttpMethod.Get, $"/v1/apps/{AppleId}/appStoreVersions$", $$$"""{"data":[{"type":"appStoreVersions","id":"ver-1","attributes":{"versionString":"1.9","appVersionState":"{{{versionState}}}","createdDate":"2026-09-01T00:00:00Z"}}]}""")
            .On(HttpMethod.Get, "/v1/appInfos/info-1/appInfoLocalizations$", """{"data":[{"type":"appInfoLocalizations","id":"il-it","attributes":{"locale":"it","name":"Meteo","subtitle":"Previsioni"}}]}""")
            .On(HttpMethod.Get, "/v1/appStoreVersions/ver-1/appStoreVersionLocalizations$", """{"data":[{"type":"appStoreVersionLocalizations","id":"vl-it","attributes":{"locale":"it","description":"Descrizione","keywords":"meteo,pioggia","promotionalText":"Vecchio"}}]}""")
            .On(HttpMethod.Patch, "/v1/appStoreVersionLocalizations/vl-it$", """{"data":{"type":"appStoreVersionLocalizations","id":"vl-it"}}""");
    }

    private Task<HttpResponseMessage> SaveAppleAsync(Account a, Guid project, string description, string promo) =>
        a.Client.PutAsJsonAsync($"/api/v1/orgs/{a.OrgId}/projects/{project}/listing/app-store/it", new
        {
            name = "Meteo", subtitle = "Previsioni", description, keywords = "meteo,pioggia", promotionalText = promo
        });

    [Fact]
    public async Task Con_la_versione_pubblicata_si_cambia_solo_il_testo_promozionale()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        Apple("READY_FOR_DISTRIBUTION");

        var listing = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/listing")).ReadJsonAsync();
        var apple = listing.GetProperty("appStore").GetProperty("data");
        Assert.False(apple.GetProperty("versionEditable").GetBoolean());
        Assert.Equal("Previsioni", apple.GetProperty("locales")[0].GetProperty("subtitle").GetString());

        var promo = await SaveAppleAsync(account, project, "Descrizione", "Nuovo testo");
        Assert.Equal(HttpStatusCode.NoContent, promo.StatusCode);
        var body = _app.StoreApis.Calls(HttpMethod.Patch, "vl-it").Single().Body!;
        Assert.Contains("Nuovo testo", body);
        Assert.DoesNotContain("description", body); // solo il campo permesso

        var blocked = await SaveAppleAsync(account, project, "Descrizione cambiata", "Nuovo testo");
        Assert.Equal(HttpStatusCode.BadGateway, blocked.StatusCode);
        Assert.Contains("versione in preparazione", (await blocked.ReadJsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Con_una_versione_in_preparazione_si_salva_tutto()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        Apple("PREPARE_FOR_SUBMISSION");

        Assert.Equal(HttpStatusCode.NoContent, (await SaveAppleAsync(account, project, "Descrizione nuova", "Promo")).StatusCode);
        Assert.Contains("Descrizione nuova", _app.StoreApis.Calls(HttpMethod.Patch, "vl-it").Single().Body);
    }

    [Fact]
    public async Task I_limiti_di_apple_si_controllano_prima_di_chiamare()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        var response = await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/listing/app-store/it", new { promotionalText = new string('x', 171) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.StoreApis.Requests.Where(r => r.Url.Contains("appstoreconnect")));
    }

    [Fact]
    public async Task Su_google_la_modifica_si_conferma_con_il_commit()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        _app.StoreApis
            .On(HttpMethod.Post, $"/applications/{Package}/edits$", """{"id":"e1"}""")
            .On(HttpMethod.Put, $"/applications/{Package}/edits/e1/listings/it-IT$", """{"language":"it-IT"}""")
            .On(HttpMethod.Post, $"/applications/{Package}/edits/e1:commit$", """{"id":"e1"}""");

        var response = await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/listing/google-play/it-IT", new
        {
            title = "Meteo", shortDescription = "Il meteo in tasca", fullDescription = "Previsioni ora per ora."
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("Il meteo in tasca", _app.StoreApis.Calls(HttpMethod.Put, "/listings/it-IT").Single().Body);
        Assert.Single(_app.StoreApis.Calls(HttpMethod.Post, "e1:commit"));
    }

    [Fact]
    public async Task Uno_screenshot_su_apple_si_prenota_si_carica_a_pezzi_e_si_conferma_con_l_md5()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        Apple("PREPARE_FOR_SUBMISSION");
        var image = new byte[3000];
        Random.Shared.NextBytes(image);

        _app.StoreApis
            .On(HttpMethod.Get, "/v1/appStoreVersionLocalizations/vl-it/appScreenshotSets$", """{"data":[{"type":"appScreenshotSets","id":"set-67","attributes":{"screenshotDisplayType":"APP_IPHONE_67"}}]}""")
            .On(HttpMethod.Post, "/v1/appScreenshots$", """
                {"data":{"type":"appScreenshots","id":"shot-1","attributes":{"uploadOperations":[
                  {"method":"PUT","url":"https://upload.apple.test/a","offset":0,"length":2000,"requestHeaders":[{"name":"Content-Type","value":"image/png"}]},
                  {"method":"PUT","url":"https://upload.apple.test/b","offset":2000,"length":1000,"requestHeaders":[{"name":"Content-Type","value":"image/png"}]}]}}}
                """)
            .On(HttpMethod.Put, "^https://upload.apple.test/", "")
            .On(HttpMethod.Patch, "/v1/appScreenshots/shot-1$", """{"data":{"type":"appScreenshots","id":"shot-1"}}""");

        using var form = new MultipartFormDataContent
        {
            { new StringContent("AppStore"), "store" },
            { new StringContent("it"), "locale" },
            { new StringContent("APP_IPHONE_67"), "group" }
        };
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "home.png");

        var response = await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/listing/screenshots", form);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Contains("home.png", _app.StoreApis.Calls(HttpMethod.Post, "/v1/appScreenshots").Single().Body);
        var parts = _app.StoreApis.Calls(HttpMethod.Put, "upload.apple.test").ToList();
        Assert.Equal(image, parts.SelectMany(p => p.Bytes!).ToArray()); // i pezzi ricompongono il file
        Assert.Contains(Convert.ToHexStringLower(MD5.HashData(image)), _app.StoreApis.Calls(HttpMethod.Patch, "shot-1").Single().Body);
    }
}
