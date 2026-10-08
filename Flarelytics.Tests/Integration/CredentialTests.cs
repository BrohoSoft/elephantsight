using System.Net;
using System.Net.Http.Json;
using System.Text;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Flarelytics.Tests.Integration;

/// <summary>Caricamento, cifratura, uso e cancellazione delle chiavi degli store.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class CredentialTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static string Credentials(Account a) => $"/api/v1/orgs/{a.OrgId}/credentials";

    [Fact]
    public async Task La_chiave_apple_finisce_cifrata_su_disco_e_non_torna_mai_indietro()
    {
        var account = await _app.SignUpAsync();
        var p8 = TestKeys.AppleP8();

        var response = await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest(p8));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var id = (await response.ReadJsonAsync()).GetProperty("id").GetGuid();

        // Nella risposta ci sono i metadati, non il segreto.
        Assert.DoesNotContain("PRIVATE KEY", body);
        Assert.Contains("ABCDE12345", body);

        // Sul disco c'è un file, e il .p8 dentro non si legge.
        var vault = _app.Services.GetRequiredService<SecretVault>();
        var file = await File.ReadAllBytesAsync(vault.PathFor(account.OrgId, id));
        var keyBody = p8.Split('\n')[1];
        Assert.DoesNotContain(keyBody, Encoding.UTF8.GetString(file));
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(file));

        // E si decifra nella chiave di partenza.
        Assert.Equal(p8.Trim(), Encoding.UTF8.GetString(await vault.ReadAsync(account.OrgId, id, default)));
    }

    [Fact]
    public async Task Una_chiave_che_lo_store_rifiuta_non_si_salva()
    {
        var account = await _app.SignUpAsync();
        _app.AppStore.NextResult = new VerificationResult(VerificationOutcome.Rejected, "Apple non riconosce la chiave.");

        var response = await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("credential_rejected", await response.ProblemCodeAsync());
        Assert.Equal(0, (await (await account.Client.GetAsync(Credentials(account))).ReadJsonAsync()).GetArrayLength());
        Assert.False(Directory.Exists(Path.Combine(_app.SecretsDirectory, account.OrgId.ToString("N"))));
    }

    [Fact]
    public async Task Un_service_account_senza_accesso_alle_app_si_salva_con_l_avviso()
    {
        var account = await _app.SignUpAsync();
        _app.GooglePlay.NextResult = new VerificationResult(VerificationOutcome.Limited, "Invita il service account.");

        var response = await account.Client.PostAsJsonAsync(Credentials(account) + "/google-play", new
        {
            label = "Play Acme",
            serviceAccountJson = TestKeys.GoogleServiceAccountJson(),
            reportsBucket = "gs://pubsite_prod_rev_01234567890987654321/"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var credential = await response.ReadJsonAsync();
        Assert.Equal("Limited", credential.GetProperty("status").GetString());
        Assert.Equal("pubsite_prod_rev_01234567890987654321", credential.GetProperty("reportsBucket").GetString());
        Assert.Equal("flarelytics@progetto.iam.gserviceaccount.com", credential.GetProperty("clientEmail").GetString());
    }

    [Fact]
    public async Task Un_file_che_non_e_un_p8_si_rifiuta_prima_di_chiamare_apple()
    {
        var account = await _app.SignUpAsync();

        var response = await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store",
            TestKeys.AppleRequest("-----BEGIN PRIVATE KEY-----\nnon una chiave\n-----END PRIVATE KEY-----"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_store_key", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task La_stessa_chiave_non_si_carica_due_volte()
    {
        var account = await _app.SignUpAsync();
        var p8 = TestKeys.AppleP8();

        await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest(p8));
        var again = await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest(p8, "Di nuovo"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("credential_exists", await again.ProblemCodeAsync());
    }

    [Fact]
    public async Task L_elenco_delle_app_usa_il_segreto_decifrato()
    {
        var account = await _app.SignUpAsync();
        var p8 = TestKeys.AppleP8();
        _app.AppStore.Apps.Add(new StoreAppInfo("1234567890", "Acme", "com.acme.app"));

        var created = await (await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest(p8))).ReadJsonAsync();
        var apps = await (await account.Client.GetAsync($"{Credentials(account)}/{created.GetProperty("id").GetGuid()}/apps")).ReadJsonAsync();

        Assert.Equal("1234567890", apps[0].GetProperty("externalId").GetString());
        Assert.Equal(p8.Trim(), _app.AppStore.LastSecret);
    }

    [Fact]
    public async Task Una_chiave_collegata_a_un_progetto_non_si_cancella_finche_non_si_scollega()
    {
        var account = await _app.SignUpAsync();
        var credential = (await (await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest())).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name = "Acme" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var projectUrl = $"/api/v1/orgs/{account.OrgId}/projects/{project}";

        var linked = await account.Client.PutAsJsonAsync(projectUrl + "/apps", new { credentialId = credential, externalAppId = "1234567890", displayName = "Acme" });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal("AppStore", (await linked.ReadJsonAsync()).GetProperty("apps")[0].GetProperty("store").GetString());

        var blocked = await account.Client.DeleteAsync($"{Credentials(account)}/{credential}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("credential_in_use", await blocked.ProblemCodeAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await account.Client.DeleteAsync(projectUrl + "/apps/AppStore")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await account.Client.DeleteAsync($"{Credentials(account)}/{credential}")).StatusCode);

        var vault = _app.Services.GetRequiredService<SecretVault>();
        Assert.False(File.Exists(vault.PathFor(account.OrgId, credential)));
    }

    [Fact]
    public async Task Per_l_app_store_serve_l_apple_id_numerico()
    {
        var account = await _app.SignUpAsync();
        var credential = (await (await account.Client.PostAsJsonAsync(Credentials(account) + "/app-store", TestKeys.AppleRequest())).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        var project = (await (await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name = "Acme" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();

        var response = await account.Client.PutAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects/{project}/apps",
            new { credentialId = credential, externalAppId = "com.acme.app" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_app_id", await response.ProblemCodeAsync());
    }
}
