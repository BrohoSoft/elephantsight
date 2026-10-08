using Flarelytics.Core.Database;
using Flarelytics.Core.Tenancy;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// Le migration si applicano anche su un database che ha già dei dati.
/// </summary>
/// <remarks>
/// Tutti gli altri test partono da un database vuoto, dove una colonna NOT
/// NULL senza valore predefinito passa senza problemi. In produzione no: è
/// successo con <c>RecoveryCodeHashes</c>, che sul database di sviluppo, con un
/// utente dentro, impediva all'API di partire.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Ogni_migration_si_applica_sopra_dati_gia_presenti()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new FlarelyticsDbContext(
            new DbContextOptionsBuilder<FlarelyticsDbContext>().UseNpgsql(connectionString).Options,
            new TenantContext());

        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();

        // Si sale una migration per volta, e prima di ognuna si aggiunge un
        // utente: così ogni migration trova righe scritte dallo schema precedente.
        for (var i = 0; i < migrations.Count; i++)
        {
            await migrator.MigrateAsync(migrations[i]);
            await InsertUserAsync(connectionString, i);
        }

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static async Task InsertUserAsync(string connectionString, int n)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO "User" ("Id", "Email", "PasswordHash", "FullName", "SecurityStamp", "CreatedAtUtc")
            VALUES (gen_random_uuid(), @email, 'x', 'Utente', 'stamp', now())
            """, connection);
        insert.Parameters.AddWithValue("email", $"u{n}@example.com");
        await insert.ExecuteNonQueryAsync();
    }
}
