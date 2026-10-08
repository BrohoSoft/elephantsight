using System.Net;
using System.Net.Http.Json;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>Versioni e build: App Store e Google Play affiancati, letti dagli store.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class ReleaseTests(PostgresFixture postgres) : IAsyncLifetime
{
    public const string AppleId = "1234567890";
    public const string Package = "com.esempio.meteo";
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Un progetto con un'app per store, ciascuna con la sua chiave.</summary>
    public static async Task<(Account Account, Guid Project)> ProjectWithBothAppsAsync(FlarelyticsAppFactory app)
    {
        var account = await app.SignUpAsync();
        var orgUrl = $"/api/v1/orgs/{account.OrgId}";
        var apple = (await (await account.Client.PostAsJsonAsync($"{orgUrl}/credentials/app-store", TestKeys.AppleRequest())).ReadJsonAsync()).GetProperty("id").GetGuid();
        var google = (await (await account.Client.PostAsJsonAsync($"{orgUrl}/credentials/google-play", new
        {
            label = "Play", serviceAccountJson = TestKeys.GoogleServiceAccountJson(), reportsBucket = "pubsite_prod_rev_1"
        })).ReadJsonAsync()).GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"{orgUrl}/projects", new { name = "Meteo" })).ReadJsonAsync()).GetProperty("id").GetGuid();
        await account.Client.PutAsJsonAsync($"{orgUrl}/projects/{project}/apps", new { credentialId = apple, externalAppId = AppleId });
        await account.Client.PutAsJsonAsync($"{orgUrl}/projects/{project}/apps", new { credentialId = google, externalAppId = Package });
        return (account, project);
    }

    private void StoresWithReleases()
    {
        _app.StoreApis
            .On(HttpMethod.Get, $"/v1/apps/{AppleId}/appStoreVersions$", """
                {"data":[
                  {"type":"appStoreVersions","id":"v2","attributes":{"versionString":"2.0","appVersionState":"PREPARE_FOR_SUBMISSION","platform":"IOS","createdDate":"2026-10-01T10:00:00Z"}},
                  {"type":"appStoreVersions","id":"v1","attributes":{"versionString":"1.9","appStoreState":"READY_FOR_SALE","platform":"IOS","createdDate":"2026-09-01T10:00:00Z"}}
                ]}
                """)
            .On(HttpMethod.Get, "/v1/builds$", """
                {"data":[
                  {"type":"builds","id":"b1","attributes":{"version":"42","processingState":"PROCESSING","expired":false,"uploadedDate":"2026-10-02T09:00:00Z"},
                   "relationships":{"preReleaseVersion":{"data":{"type":"preReleaseVersions","id":"p1"}}}}
                ],
                 "included":[{"type":"preReleaseVersions","id":"p1","attributes":{"version":"2.0","platform":"IOS"}}]}
                """)
            .On(HttpMethod.Post, $"/applications/{Package}/edits$", """{"id":"edit-1"}""")
            .On(HttpMethod.Get, $"/applications/{Package}/edits/edit-1/tracks$", """
                {"tracks":[
                  {"track":"production","releases":[{"name":"1.9 (19)","versionCodes":["19"],"status":"inProgress","userFraction":0.2,
                    "releaseNotes":[{"language":"it-IT","text":"Correzioni"}]}]},
                  {"track":"internal","releases":[{"name":"2.0 (20)","versionCodes":["20"],"status":"completed"}]}
                ]}
                """)
            .On(HttpMethod.Get, $"/applications/{Package}/edits/edit-1/bundles$", """{"bundles":[{"versionCode":19},{"versionCode":20},{"versionCode":18}]}""")
            .On(HttpMethod.Delete, $"/applications/{Package}/edits/edit-1$", "");
    }

    [Fact]
    public async Task Versioni_e_build_dei_due_store_affiancate()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        StoresWithReleases();

        var releases = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/releases")).ReadJsonAsync();

        var apple = releases[0];
        Assert.Equal("AppStore", apple.GetProperty("store").GetString());
        Assert.Equal("2.0", apple.GetProperty("versions")[0].GetProperty("version").GetString());   // la più recente prima
        Assert.Equal("Draft", apple.GetProperty("versions")[0].GetProperty("stage").GetString());
        Assert.Equal("Live", apple.GetProperty("versions")[1].GetProperty("stage").GetString());    // dal campo storico appStoreState
        var build = apple.GetProperty("builds")[0];
        Assert.Equal(("2.0", "42", "Processing"), (build.GetProperty("version").GetString(), build.GetProperty("buildNumber").GetString(), build.GetProperty("stage").GetString()));

        var google = releases[1];
        var production = google.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("track").GetString() == "production");
        Assert.Equal("Rolling", production.GetProperty("stage").GetString());
        Assert.Equal(20, production.GetProperty("rolloutPercent").GetDouble(), 3);
        Assert.Equal("Correzioni", production.GetProperty("releaseNotes").GetString());
        Assert.Equal(["20", "19", "18"], google.GetProperty("builds").EnumerateArray().Select(b => b.GetProperty("buildNumber").GetString()));

        // Una lettura non deve lasciare edit aperti né confermati su Google Play.
        Assert.Single(_app.StoreApis.Calls(HttpMethod.Delete, "/edits/edit-1"));
        Assert.Empty(_app.StoreApis.Calls(HttpMethod.Post, ":commit"));
    }

    [Fact]
    public async Task Se_uno_store_rifiuta_l_altro_si_vede_lo_stesso()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        StoresWithReleases();
        _app.StoreApis.On(HttpMethod.Get, $"/v1/apps/{AppleId}/appStoreVersions$",
            """{"errors":[{"detail":"The API key in use does not allow this request"}]}""", HttpStatusCode.Forbidden);

        var releases = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/releases")).ReadJsonAsync();

        Assert.Contains("Admin o App Manager", releases[0].GetProperty("error").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, releases[1].GetProperty("error").ValueKind);
        Assert.Equal(2, releases[1].GetProperty("versions").GetArrayLength());
    }
}
