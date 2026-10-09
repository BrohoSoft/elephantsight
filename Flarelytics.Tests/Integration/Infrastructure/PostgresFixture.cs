using Npgsql;
using Testcontainers.PostgreSql;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// Un PostgreSQL per tutta la collezione, e un database nuovo per ogni test.
/// </summary>
/// <remarks>
/// L'applicazione si collega con un ruolo <b>non superutente</b>, proprietario
/// del database, come in produzione. Con l'utente <c>postgres</c> del
/// contenitore la Row-Level Security non scatterebbe mai (i superutenti la
/// ignorano sempre) e i test sull'isolamento passerebbero senza provare niente.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppRole = "flarelytics_app";
    private const string AppPassword = "app";

    // Ogni test ha il suo database e i suoi pool di connessioni: con le 100
    // connessioni di serie, a suite lunga, PostgreSQL rifiuta le nuove.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=400")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await _container.ExecScriptAsync($"CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOBYPASSRLS;");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await _container.ExecScriptAsync($"CREATE DATABASE \"{name}\" OWNER {AppRole};");

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            Username = AppRole,
            Password = AppPassword
        }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
