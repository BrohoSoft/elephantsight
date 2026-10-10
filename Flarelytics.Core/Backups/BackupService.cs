using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flarelytics.Core.Database;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Backups;

/// <summary>Un file di backup sul disco.</summary>
public record BackupFile(string Name, long SizeBytes, DateTime CreatedAtUtc);

/// <summary>
/// Crea i backup: database (<c>pg_dump</c>), chiavi, credenziali cifrate e
/// report degli store, in un file solo cifrato con la password dell'istanza.
/// Niente passa in chiaro dal disco: il dump va dritto nell'archivio, che va
/// dritto nella cifratura.
/// </summary>
/// <remarks>
/// <para>Il dump si fa con un ruolo a parte (<c>ConnectionStrings:Backup</c>,
/// <c>flarelytics_backup</c>): di sola lettura e con BYPASSRLS, perché il ruolo
/// dell'app vede un tenant alla volta e non deve poter fare diversamente.</para>
///
/// <para>Restano fuori i file dei post (su Bunny restano lì; sul disco si
/// rifanno), i temporanei e le build in coda.</para>
/// </remarks>
public partial class BackupService(
    FlarelyticsDbContext db, PgTools pg, IConfiguration configuration, IOptionsMonitor<BackupOptions> options,
    IOptions<SecretsOptions> secrets, IOptions<ReportsOptions> reports, ILogger<BackupService> log)
{
    public const string Extension = ".esbk";

    /// <summary>Le cartelle dei report che non vanno nel backup: file dei post, temporanei, build in coda.</summary>
    private static readonly string[] SkippedReportFolders = ["_social", "_social-tmp", "_uploads"];

    [GeneratedRegex(@"^elephantsight-\d{8}-\d{9}\.esbk$")]
    public static partial Regex FileNamePattern();

    public string Directory => options.CurrentValue.Directory is { Length: > 0 } d
        ? Path.GetFullPath(d)
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reports.Value.StorageDirectory))!, "backups");

    public IReadOnlyList<BackupFile> Files() =>
        System.IO.Directory.Exists(Directory)
            ? new DirectoryInfo(Directory).EnumerateFiles("*" + Extension)
                .Where(f => FileNamePattern().IsMatch(f.Name))
                .Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(f => f.Name)
                .ToList()
            : [];

    /// <summary>Il percorso di un file di backup, solo se il nome è uno dei nostri (niente <c>../</c>).</summary>
    public string? PathOf(string name) =>
        FileNamePattern().IsMatch(name) && File.Exists(Path.Combine(Directory, name)) ? Path.Combine(Directory, name) : null;

    public async Task<BackupRun> RunAsync(bool manual, CancellationToken ct)
    {
        var run = BackupRun.Start(manual, DateTime.UtcNow);
        db.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var (name, size) = await CreateAsync(ct);
            run.Succeed(name, size, DateTime.UtcNow);
            log.LogInformation("Backup {Kind} completato: {File}, {Megabytes:0.0} MB", manual ? "manuale" : "programmato", name, size / 1024d / 1024d);
            DeleteOld();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            run.Fail(e is BackupException or IOException or UnauthorizedAccessException ? e.Message : "Errore interno: " + e.Message, DateTime.UtcNow);
            log.LogError(e, "Backup {Kind} non riuscito: {Message}", manual ? "manuale" : "programmato", e.Message);
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return run;
    }

    private async Task<(string Name, long Size)> CreateAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) throw new BackupException("I backup sono spenti in questa installazione.");
        if (!o.HasPassword) throw new BackupException("Manca la password dei backup: impostala nelle impostazioni dell'istanza.");
        var connection = configuration.GetConnectionString("Backup");
        if (string.IsNullOrWhiteSpace(connection)) throw new BackupException("Manca la connessione al database per i backup (ConnectionStrings:Backup).");

        System.IO.Directory.CreateDirectory(Directory);
        var name = $"elephantsight-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{Extension}";
        var path = Path.Combine(Directory, name);
        var part = path + ".part";
        try
        {
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            await using (var encrypted = BackupCipher.Encrypt(file, o.Password!))
            {
                var archive = new BackupArchiveWriter(encrypted);
                await using (var manifest = archive.Add("manifest.json"))
                {
                    await JsonSerializer.SerializeAsync(manifest, new
                    {
                        format = 1,
                        createdAtUtc = DateTime.UtcNow,
                        version = typeof(BackupService).Assembly.GetName().Version?.ToString(3),
                        contents = new[] { "database", "keys", "secrets", "reports" }
                    }, cancellationToken: ct);
                }

                await AddDirectoryAsync(archive, "keys", secrets.Value.KeysDirectory, [], ct);
                await AddDirectoryAsync(archive, "secrets", secrets.Value.StorageDirectory, [], ct);
                await AddDirectoryAsync(archive, "reports", reports.Value.StorageDirectory, SkippedReportFolders, ct);

                // Il database per ultimo: il ripristino rimette prima le chiavi, poi i dati che le usano.
                await using (var dump = archive.Add("database.dump"))
                {
                    await pg.DumpAsync(connection, dump, ct);
                }
                archive.Complete();
            }
            File.Move(part, path);
            return (name, new FileInfo(path).Length);
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { }
            throw;
        }
    }

    private async Task AddDirectoryAsync(BackupArchiveWriter archive, string prefix, string? root, string[] skipped, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return;
        var full = Path.GetFullPath(root);
        var backups = Directory + Path.DirectorySeparatorChar;
        foreach (var file in System.IO.Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/');
            if (skipped.Any(s => relative.StartsWith(s + "/", StringComparison.Ordinal))) continue;
            if (file.StartsWith(backups, StringComparison.Ordinal) || file.EndsWith(".part", StringComparison.Ordinal)) continue;
            FileStream content;
            try
            {
                content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            }
            catch (FileNotFoundException)
            {
                continue; // cancellato mentre si leggeva la cartella (un temporaneo)
            }
            await using (content)
            {
                await archive.AddAsync($"{prefix}/{relative}", content, ct);
            }
        }
    }

    /// <summary>Tiene gli ultimi <see cref="BackupOptions.Keep"/> file.</summary>
    public void DeleteOld()
    {
        var keep = Math.Max(1, options.CurrentValue.Keep);
        foreach (var old in Files().Skip(keep))
        {
            File.Delete(Path.Combine(Directory, old.Name));
            log.LogInformation("Backup vecchio cancellato: {File}", old.Name);
        }
    }

    public void Delete(string name)
    {
        if (PathOf(name) is { } path) File.Delete(path);
    }
}

/// <summary>
/// Un backup alla volta, che sia programmato o chiesto dal pannello; quello
/// del pannello gira in background (un dump grande dura più di una richiesta).
/// </summary>
public class BackupRunner(IServiceScopeFactory scopes, ILogger<BackupRunner> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool Running => _lock.CurrentCount == 0;

    /// <summary>Esegue un backup se non ce n'è già uno in corso. Falso: era occupato.</summary>
    public async Task<bool> TryRunAsync(bool manual, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct)) return false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(manual, ct);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Avvia un backup manuale senza aspettarlo.</summary>
    public bool StartManual()
    {
        if (Running) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await TryRunAsync(manual: true, CancellationToken.None);
            }
            catch (Exception e)
            {
                log.LogError(e, "Backup manuale non riuscito");
            }
        });
        return true;
    }
}

/// <summary>Ogni minuto guarda se è arrivato l'appuntamento del backup programmato.</summary>
public class BackupWorker(IServiceScopeFactory scopes, BackupRunner runner, IOptionsMonitor<BackupOptions> options,
    ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CloseInterruptedAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Controllo dei backup programmati non riuscito");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// All'avvio nessun backup è in corso (l'istanza è una sola): quelli
    /// rimasti aperti si sono fermati con l'app, o vengono da un ripristino
    /// (il dump li vede mentre si fanno). Altrimenti resterebbero "in corso".
    /// </summary>
    public async Task CloseInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        var open = await db.Set<BackupRun>().Where(r => r.FinishedAtUtc == null).ToListAsync(ct);
        foreach (var run in open) run.Fail("Interrotto: l'app si è fermata durante il backup (oppure è il backup in corso al momento del dump ripristinato).", DateTime.UtcNow);
        if (open.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fa il backup se l'ultimo appuntamento è passato e da allora non ne è
    /// partito nessuno (nemmeno uno fallito: si riprova all'appuntamento dopo, o a mano).
    /// </summary>
    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled || !o.Active || !o.HasPassword || BackupSchedule.LatestSlotUtc(o, nowUtc) is not { } slot) return false;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            if (await db.Set<BackupRun>().AnyAsync(r => !r.Manual && r.StartedAtUtc >= slot, ct)) return false;
        }
        return await runner.TryRunAsync(manual: false, ct);
    }
}
