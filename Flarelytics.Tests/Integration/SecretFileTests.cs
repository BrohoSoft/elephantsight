using System.Net;
using System.Net.Http.Json;
using System.Text;
using Flarelytics.Api.Auth;
using Flarelytics.Core.Secrets;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Flarelytics.Tests.Integration;

/// <summary>La cassaforte dei file di firma: cifrati, riscaricabili solo confermando chi si è.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SecretFileTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<Guid> ProjectAsync(Account a) =>
        (await (await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/projects", new { name = "Meteo" })).ReadJsonAsync()).GetProperty("id").GetGuid();

    private static async Task<Guid> UploadKeystoreAsync(Account a, Guid project, byte[] content)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Android"), "platform" },
            { new StringContent("AndroidKeystore"), "kind" },
            { new StringContent("Keystore di rilascio"), "name" },
            { new ByteArrayContent(content), "file", "release.jks" }
        };
        var response = await a.Client.PostAsync($"/api/v1/orgs/{a.OrgId}/projects/{project}/files", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Il_keystore_finisce_cifrato_e_torna_identico_con_la_password()
    {
        var account = await _app.SignUpAsync();
        var project = await ProjectAsync(account);
        var keystore = Encoding.UTF8.GetBytes("KEYSTORE-SEGRETO-" + new string('x', 500));

        var id = await UploadKeystoreAsync(account, project, keystore);

        // Sul disco non si legge.
        var path = _app.Services.GetRequiredService<SecretVault>().PathFor(account.OrgId, id);
        Assert.DoesNotContain("KEYSTORE-SEGRETO", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));

        var wrong = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files/{id}/download", new { password = "sbagliata-123" });
        Assert.Equal("invalid_password", await wrong.ProblemCodeAsync());

        var right = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files/{id}/download", new { password = TestApi.Password });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(keystore, await right.Content.ReadAsByteArrayAsync());
        Assert.Equal("release.jks", right.Content.Headers.ContentDisposition?.FileName);

        var list = await (await account.Client.GetAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files")).ReadJsonAsync();
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, list[0].GetProperty("lastDownloadedAtUtc").ValueKind);
    }

    [Fact]
    public async Task Con_la_2fa_attiva_serve_anche_il_codice()
    {
        var account = await _app.SignUpAsync();
        var project = await ProjectAsync(account);
        var id = await UploadKeystoreAsync(account, project, [1, 2, 3]);

        var setup = await (await account.Client.PostAsJsonAsync("/api/v1/me/2fa/setup", new { password = TestApi.Password })).ReadJsonAsync();
        var secret = Base32.Decode(setup.GetProperty("secret").GetString()!);
        await account.Client.PostAsJsonAsync("/api/v1/me/2fa/enable", new { code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow)) });

        var noCode = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files/{id}/download", new { password = TestApi.Password });
        Assert.Equal("invalid_code", await noCode.ProblemCodeAsync());

        var withCode = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files/{id}/download",
            new { password = TestApi.Password, code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow) + 1) });
        Assert.Equal(new byte[] { 1, 2, 3 }, await withCode.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Un_testo_incollato_diventa_un_file()
    {
        var account = await _app.SignUpAsync();
        var project = await ProjectAsync(account);

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Android"), "platform" },
            { new StringContent("KeyProperties"), "kind" },
            { new StringContent("key.properties"), "name" },
            { new StringContent("key.properties"), "fileName" },
            { new StringContent("storePassword=segreta\nkeyAlias=upload"), "text" }
        };
        var created = await (await account.Client.PostAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files", form)).ReadJsonAsync();
        var id = created.GetProperty("id").GetGuid();

        var download = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/files/{id}/download", new { password = TestApi.Password });
        Assert.Equal("storePassword=segreta\nkeyAlias=upload", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cancellare_il_progetto_cancella_anche_i_file_cifrati()
    {
        var account = await _app.SignUpAsync();
        var project = await ProjectAsync(account);
        var id = await UploadKeystoreAsync(account, project, [9, 9, 9]);
        var path = _app.Services.GetRequiredService<SecretVault>().PathFor(account.OrgId, id);

        await account.Client.DeleteAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}");

        Assert.False(File.Exists(path));
    }
}
