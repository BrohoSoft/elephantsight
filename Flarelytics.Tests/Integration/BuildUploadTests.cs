using System.Net;
using System.Net.Http.Headers;
using Flarelytics.Core.Management;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Flarelytics.Tests.Integration.ReleaseTests;

namespace Flarelytics.Tests.Integration;

/// <summary>Le build caricate dal pannello: in coda, poi allo store con il worker.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class BuildUploadTests(PostgresFixture postgres) : IAsyncLifetime
{
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

    private BuildUploadWorker Worker => _sync.Services.GetServices<IHostedService>().OfType<BuildUploadWorker>().Single();

    private static MultipartFormDataContent Form(string store, byte[] file, string fileName, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new StringContent(store), "store" } };
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        return form;
    }

    private static async Task<System.Text.Json.JsonElement> UploadsAsync(Account a, Guid project) =>
        await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/projects/{project}/builds/uploads")).ReadJsonAsync();

    [Fact]
    public async Task Un_aab_va_nel_canale_scelto_con_rilascio_graduale_e_note()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        var bundle = new byte[50_000];
        Random.Shared.NextBytes(bundle);
        _app.StoreApis
            .On(HttpMethod.Post, $"/applications/{Package}/edits$", """{"id":"e9"}""")
            .On(HttpMethod.Post, $"/upload/androidpublisher/v3/applications/{Package}/edits/e9/bundles$", """{"versionCode":21,"sha256":"x"}""")
            .On(HttpMethod.Put, $"/applications/{Package}/edits/e9/tracks/production$", """{"track":"production"}""")
            .On(HttpMethod.Post, $"/applications/{Package}/edits/e9:commit$", """{"id":"e9"}""");

        var response = await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/builds/uploads",
            Form("GooglePlay", bundle, "app-release.aab", ("track", "production"), ("releaseStatus", "inProgress"), ("rolloutPercent", "10"),
                ("releaseName", "2.1.0"), ("releaseNotesLanguage", "it-IT"), ("releaseNotes", "Nuovo radar")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Queued", (await UploadsAsync(account, project))[0].GetProperty("status").GetString());

        await Worker.RunOnceAsync(CancellationToken.None);

        var upload = (await UploadsAsync(account, project))[0];
        Assert.Equal("Completed", upload.GetProperty("status").GetString());
        Assert.Equal("21", upload.GetProperty("buildNumber").GetString());

        Assert.Equal(bundle, _app.StoreApis.Calls(HttpMethod.Post, "/edits/e9/bundles").Single().Bytes);
        var track = _app.StoreApis.Calls(HttpMethod.Put, "/tracks/production").Single().Body!;
        Assert.Contains("\"inProgress\"", track);
        Assert.Contains("0.1", track);           // 10% → userFraction 0.1
        Assert.Contains("Nuovo radar", track);
        Assert.Single(_app.StoreApis.Calls(HttpMethod.Post, "e9:commit"));
        Assert.Empty(Directory.GetFiles(_sync.Services.GetRequiredService<UploadStorage>().Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Un_ipa_si_carica_con_l_api_di_apple_e_si_segue_l_elaborazione()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        var ipa = Ipa.Create(Ipa.BinaryPlist);
        _app.StoreApis
            .On(HttpMethod.Post, "/v1/buildUploads$", """{"data":{"type":"buildUploads","id":"bu-1"}}""")
            .On(HttpMethod.Post, "/v1/buildUploadFiles$", $$$$"""
                {"data":{"type":"buildUploadFiles","id":"bf-1","attributes":{"uploadOperations":[
                  {"method":"PUT","url":"https://upload.apple.test/ipa","offset":0,"length":{{{{ipa.Length}}}},"requestHeaders":[]}]}}}
                """)
            .On(HttpMethod.Put, "^https://upload.apple.test/ipa$", "")
            .On(HttpMethod.Patch, "/v1/buildUploadFiles/bf-1$", """{"data":{"type":"buildUploadFiles","id":"bf-1"}}""")
            .On(HttpMethod.Get, "/v1/buildUploads/bu-1$", """{"data":{"type":"buildUploads","id":"bu-1","attributes":{"state":{"state":"COMPLETE"}}}}""");

        var response = await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/builds/uploads", Form("AppStore", ipa, "Meteo.ipa"));
        var queued = await response.ReadJsonAsync();
        Assert.Equal(("2.1.0", "137"), (queued.GetProperty("version").GetString(), queued.GetProperty("buildNumber").GetString())); // dall'Info.plist

        await Worker.RunOnceAsync(CancellationToken.None);

        var create = _app.StoreApis.Calls(HttpMethod.Post, "/v1/buildUploads").Single().Body!;
        Assert.Contains("\"cfBundleVersion\":\"137\"", create);
        Assert.Contains($"\"{AppleId}\"", create);
        Assert.Contains("com.apple.ipa", _app.StoreApis.Calls(HttpMethod.Post, "/v1/buildUploadFiles").Single().Body);
        Assert.Equal(ipa, _app.StoreApis.Calls(HttpMethod.Put, "upload.apple.test").Single().Bytes);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(ipa)), _app.StoreApis.Calls(HttpMethod.Patch, "bf-1").Single().Body);
        Assert.Equal("Completed", (await UploadsAsync(account, project))[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Un_errore_di_google_lascia_il_caricamento_fallito_con_il_motivo_e_niente_commit()
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        _app.StoreApis
            .On(HttpMethod.Post, $"/applications/{Package}/edits$", """{"id":"e5"}""")
            .On(HttpMethod.Post, $"/upload/androidpublisher/v3/applications/{Package}/edits/e5/bundles$",
                """{"error":{"message":"APK specifies a version code that has already been used."}}""", HttpStatusCode.BadRequest)
            .On(HttpMethod.Delete, $"/applications/{Package}/edits/e5$", "");

        await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/builds/uploads", Form("GooglePlay", [1, 2, 3], "app.aab"));
        await Worker.RunOnceAsync(CancellationToken.None);

        var upload = (await UploadsAsync(account, project))[0];
        Assert.Equal("Failed", upload.GetProperty("status").GetString());
        Assert.Contains("version code", upload.GetProperty("message").GetString());
        Assert.Empty(_app.StoreApis.Calls(HttpMethod.Post, ":commit"));
        Assert.Single(_app.StoreApis.Calls(HttpMethod.Delete, "/edits/e5"));
    }

    [Theory]
    [InlineData("GooglePlay", "app.apk")]
    [InlineData("AppStore", "app.aab")]
    public async Task Un_file_del_tipo_sbagliato_si_rifiuta_subito(string store, string fileName)
    {
        var (account, project) = await ProjectWithBothAppsAsync(_app);
        var response = await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/builds/uploads", Form(store, [1, 2, 3], fileName));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
