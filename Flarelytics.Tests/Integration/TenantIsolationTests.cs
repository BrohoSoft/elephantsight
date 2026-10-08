using System.Net;
using System.Net.Http.Json;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Npgsql;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// Un'organizzazione non vede i dati di un'altra: né dall'API, né dal database
/// con una query che scavalca EF.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class TenantIsolationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _app = new FlarelyticsAppFactory(_connectionString);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<Guid> CreateProjectAsync(Account account, string name)
    {
        var response = await account.Client.PostAsJsonAsync($"/api/v1/orgs/{account.OrgId}/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task L_organizzazione_di_un_altro_risponde_404()
    {
        var alice = await _app.SignUpAsync();
        var bob = await _app.SignUpAsync();
        await CreateProjectAsync(alice, "Segreto");

        // 404 e non 403: un 403 direbbe che quell'organizzazione esiste.
        var response = await bob.Client.GetAsync($"/api/v1/orgs/{alice.OrgId}/projects");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Un_progetto_di_un_altro_tenant_non_si_trova_neanche_dalla_propria_organizzazione()
    {
        var alice = await _app.SignUpAsync();
        var bob = await _app.SignUpAsync();
        var project = await CreateProjectAsync(alice, "Segreto");

        // Bob usa la sua organizzazione, ma l'id del progetto di Alice.
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.Client.GetAsync($"/api/v1/orgs/{bob.OrgId}/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.Client.DeleteAsync($"/api/v1/orgs/{bob.OrgId}/projects/{project}")).StatusCode);

        var list = await (await bob.Client.GetAsync($"/api/v1/orgs/{bob.OrgId}/projects")).ReadJsonAsync();
        Assert.Equal(0, list.GetArrayLength());
    }

    /// <summary>
    /// La seconda linea di difesa: SQL scritto a mano, senza il filtro di EF,
    /// con lo stesso ruolo con cui si collega l'applicazione.
    /// </summary>
    [Fact]
    public async Task La_row_level_security_filtra_anche_le_query_che_non_passano_da_ef()
    {
        var alice = await _app.SignUpAsync();
        var bob = await _app.SignUpAsync();
        await CreateProjectAsync(alice, "Di Alice");

        Assert.Equal(1, await CountProjectsAsAsync(alice.OrgId));
        Assert.Equal(0, await CountProjectsAsAsync(bob.OrgId));

        // Nessun tenant impostato: la tabella appare vuota, non piena.
        Assert.Equal(0, await CountProjectsAsAsync(null));
    }

    [Fact]
    public async Task La_row_level_security_rifiuta_una_scrittura_su_un_altro_tenant()
    {
        var alice = await _app.SignUpAsync();
        var bob = await _app.SignUpAsync();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, bob.OrgId);

        await using var insert = new NpgsqlCommand(
            """INSERT INTO "Project" ("Id", "TenantId", "Name", "CreatedAtUtc") VALUES (gen_random_uuid(), @t, 'Intruso', now())""",
            connection);
        insert.Parameters.AddWithValue("t", alice.OrgId);

        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task<long> CountProjectsAsAsync(Guid? tenant)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        if (tenant is { } t) await SetTenantAsync(connection, t);

        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "Project" """, connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenant)
    {
        await using var set = new NpgsqlCommand($"SELECT set_config('{TenantConnectionInterceptor.SettingName}', @t, false)", connection);
        set.Parameters.AddWithValue("t", tenant.ToString());
        await set.ExecuteNonQueryAsync();
    }
}
