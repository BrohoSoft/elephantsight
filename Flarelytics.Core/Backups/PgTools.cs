using System.Diagnostics;
using Npgsql;

namespace Flarelytics.Core.Backups;

public class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <c>pg_dump</c> e <c>pg_restore</c> (nell'immagine Docker, pacchetto
/// <c>postgresql-client</c>). La connessione passa dalle variabili <c>PG*</c>,
/// non dalla riga di comando: la password non compare fra i processi.
/// </summary>
public class PgTools
{
    /// <summary>Il dump completo in formato custom (già compresso), scritto in <paramref name="output"/> mentre arriva.</summary>
    public Task DumpAsync(string connectionString, Stream output, CancellationToken ct) =>
        RunAsync("pg_dump", connectionString, ["--format=custom", "--compress=6", "--no-owner", "--no-privileges"], input: null, output, ct);

    /// <summary>
    /// Ripristina un dump nel database della connessione, sostituendo quello che
    /// c'è. Con il ruolo dell'app: le politiche della RLS si creano dopo i
    /// dati, quindi il caricamento non le incontra.
    /// </summary>
    public Task RestoreAsync(string connectionString, Stream dump, CancellationToken ct) =>
        RunAsync("pg_restore", connectionString,
            ["--clean", "--if-exists", "--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error", "--dbname=" + new NpgsqlConnectionStringBuilder(connectionString).Database],
            dump, Stream.Null, ct);

    /// <summary>Come si avvia lo strumento. I test lo eseguono dentro il contenitore di PostgreSQL.</summary>
    protected virtual ProcessStartInfo StartInfo(string tool, NpgsqlConnectionStringBuilder connection, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(tool);
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["PGHOST"] = connection.Host;
        info.Environment["PGPORT"] = connection.Port.ToString();
        info.Environment["PGUSER"] = connection.Username;
        info.Environment["PGPASSWORD"] = connection.Password;
        info.Environment["PGDATABASE"] = connection.Database;
        return info;
    }

    private async Task RunAsync(string tool, string connectionString, IReadOnlyList<string> arguments, Stream? input, Stream output, CancellationToken ct)
    {
        var info = StartInfo(tool, new NpgsqlConnectionStringBuilder(connectionString), arguments);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = input is not null;
        info.UseShellExecute = false;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new BackupException($"{tool} non si avvia.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new BackupException($"{tool} non è installato su questa macchina (nell'immagine Docker c'è).", e);
        }

        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
            if (input is not null)
            {
                try
                {
                    await input.CopyToAsync(process.StandardInput.BaseStream, ct);
                }
                catch (IOException)
                {
                    // pg_restore ha chiuso l'ingresso per un errore: lo dice nello stderr, qui sotto.
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }
            await copyOut;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new BackupException(Explain(tool, connectionString, (await errors).Trim()));
        }
    }

    /// <summary>Gli errori più probabili, spiegati; gli altri come li scrive PostgreSQL.</summary>
    private static string Explain(string tool, string connectionString, string stderr)
    {
        var user = new NpgsqlConnectionStringBuilder(connectionString).Username;
        if (stderr.Contains("password authentication failed") || stderr.Contains("role \"" + user + "\" does not exist"))
            return $"Il database non accetta l'utente {user}. Nelle installazioni fatte prima dei backup il ruolo va creato una volta: " +
                   "docker compose exec postgres bash /docker-entrypoint-initdb.d/20-backup-role.sh";
        if (stderr.Contains("server version") && stderr.Contains("aborting because of server version mismatch"))
            return $"{tool} è più vecchio del server PostgreSQL: {stderr}";
        return $"{tool} non è riuscito: {(stderr.Length > 1500 ? stderr[..1500] : stderr)}";
    }
}
