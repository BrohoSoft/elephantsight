using System.Security.Cryptography;
using Flarelytics.Core.Backups;

namespace Flarelytics.Api.Features.Backups;

/// <summary>
/// Il ripristino da riga di comando, a istanza ferma:
/// <c>docker compose run --rm app restore /percorso/del/file.esbk</c>.
/// Con <c>--check</c> verifica il file senza scrivere niente; con <c>--yes</c>
/// non chiede conferma. La password si scrive quando la chiede (o in
/// <c>BACKUP_PASSWORD</c>, per gli script).
/// </summary>
/// <remarks>
/// Non dal pannello: sostituisce database e chiavi, e farlo mentre l'app gira
/// (o farlo fare a un cliente di un'istanza ospitata) è un modo di perdere dati.
/// </remarks>
public static class BackupCommand
{
    public const string Name = "restore";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, IConfiguration configuration)
    {
        var check = args.Contains("--check");
        var yes = args.Contains("--yes");
        var path = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("Uso: restore [--check] [--yes] <file .esbk>");
            Console.Error.WriteLine(path is null ? "Manca il file." : $"Il file {path} non esiste (dentro il container: montalo con -v).");
            return 2;
        }

        var password = Environment.GetEnvironmentVariable("BACKUP_PASSWORD") is { Length: > 0 } fromEnv ? fromEnv : ReadPassword("Password dei backup: ");
        await using var scope = services.CreateAsyncScope();
        var restorer = scope.ServiceProvider.GetRequiredService<BackupRestorer>();

        try
        {
            Console.WriteLine("Verifico il file…");
            var summary = await restorer.VerifyAsync(path, password, CancellationToken.None);
            Console.WriteLine($"Il backup è integro: {summary.Files} file ({string.Join(", ", summary.Areas)}), " +
                              $"{(summary.HasDatabase ? "con" : "SENZA")} database, {summary.Bytes / 1024d / 1024d:0.0} MB.");
            if (check) return 0;

            if (!yes)
            {
                Console.Write("Sostituisco database, chiavi, credenziali e report di questa installazione con quelli del backup. L'app deve essere ferma. Continuo? (si/no) ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("si" or "sì" or "s" or "yes" or "y"))
                {
                    Console.WriteLine("Niente di fatto.");
                    return 1;
                }
            }

            var connection = configuration.GetConnectionString("Database") ?? throw new BackupException("Manca ConnectionStrings:Database.");
            await restorer.RestoreAsync(path, password, connection, CancellationToken.None);
            Console.WriteLine("Ripristino completato. Avvia l'app: docker compose up -d");
            return 0;
        }
        catch (CryptographicException)
        {
            Console.Error.WriteLine("Il file non si apre: password sbagliata, oppure il file è danneggiato o incompleto.");
            return 3;
        }
        catch (Exception e) when (e is BackupException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 4;
        }
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.WriteLine();
        return password.ToString();
    }
}
