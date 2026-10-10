using System.Diagnostics;
using Flarelytics.Core.Backups;
using Npgsql;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// pg_dump e pg_restore eseguiti dentro il contenitore di PostgreSQL dei test
/// (<c>docker exec</c>), con la stessa versione del server: sul computer di
/// chi lancia i test possono mancare. Gli argomenti sono quelli veri.
/// </summary>
public class ContainerPgTools(string containerId) : PgTools
{
    protected override ProcessStartInfo StartInfo(string tool, NpgsqlConnectionStringBuilder connection, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("docker");
        foreach (var a in new[]
                 {
                     "exec", "-i",
                     "-e", "PGHOST=localhost", "-e", "PGPORT=5432",
                     "-e", $"PGUSER={connection.Username}", "-e", $"PGPASSWORD={connection.Password}", "-e", $"PGDATABASE={connection.Database}",
                     containerId, tool
                 })
        {
            info.ArgumentList.Add(a);
        }
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return info;
    }
}
