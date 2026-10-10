using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flarelytics.Core.Backups;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Flarelytics.Tests.Integration;

/// <summary>
/// I backup dell'istanza: la funzione spenta dall'ambiente, la programmazione
/// dal pannello, il file cifrato, il ripristino su un database e cartelle
/// nuovi (come su un server nuovo), quanti file si tengono, il worker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class BackupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string BackupPassword = "la-password-dei-backup";

    private string _connection = null!;
    private FlarelyticsAppFactory _app = null!;
    private readonly string _restoreRoot = Path.Combine(Path.GetTempPath(), "flarelytics-restore-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync() => _connection = await postgres.CreateDatabaseAsync();

    private void Start(bool enabled = true, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backups:Enabled"] = enabled ? "true" : "false",
            ["ConnectionStrings:Backup"] = PostgresFixture.BackupConnection(_connection),
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        _app = new FlarelyticsAppFactory(_connection, settings)
        {
            TestServices = s =>
            {
                s.RemoveAll<PgTools>();
                s.AddSingleton<PgTools>(new ContainerPgTools(postgres.ContainerId));
            }
        };
        _ = _app.Services;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(_restoreRoot)) Directory.Delete(_restoreRoot, recursive: true);
    }

    private async Task<Account> InstanceAdminAsync()
    {
        var a = await _app.SignUpAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
        (await db.Set<User>().SingleAsync(u => u.Email == a.Email)).SetInstanceAdmin(true);
        await db.SaveChangesAsync();
        return a;
    }

    private async Task<JsonElement> StatusAsync(Account a) => await (await a.Client.GetAsync("/api/v1/instance/backups")).ReadJsonAsync();

    /// <summary>Un backup dal pannello, aspettando che finisca (gira in background).</summary>
    private async Task<JsonElement> RunAndWaitAsync(Account a)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await a.Client.PostAsync("/api/v1/instance/backups/run", null)).StatusCode);
        for (var i = 0; i < 300; i++)
        {
            var status = await StatusAsync(a);
            if (!status.GetProperty("running").GetBoolean() && status.GetProperty("runs").GetArrayLength() > 0
                && status.GetProperty("runs")[0].GetProperty("finishedAtUtc").ValueKind != JsonValueKind.Null)
                return status;
            await Task.Delay(100);
        }
        throw new TimeoutException("Il backup non è finito.");
    }

    private string KeysDirectory => _app.KeysDirectory;
    private string ReportsDirectory => _app.Services.GetRequiredService<IOptions<ReportsOptions>>().Value.StorageDirectory;

    [Fact]
    public async Task Con_BACKUPS_ENABLED_falso_la_funzione_non_esiste()
    {
        Start(enabled: false);
        var admin = await InstanceAdminAsync();

        var instance = await (await _app.CreateClient().GetAsync("/api/v1/instance")).ReadJsonAsync();
        Assert.False(instance.GetProperty("backupsEnabled").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/api/v1/instance/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsync("/api/v1/instance/backups/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup",
            new { active = "true", password = BackupPassword })).StatusCode);
        var groups = await (await admin.Client.GetAsync("/api/v1/instance/settings")).ReadJsonAsync();
        Assert.DoesNotContain(groups.EnumerateArray(), g => g.GetProperty("group").GetString() == "backup");
    }

    [Fact]
    public async Task Solo_un_amministratore_dell_istanza_vede_i_backup()
    {
        Start();
        var owner = await _app.SignUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.GetAsync("/api/v1/instance/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.PostAsync("/api/v1/instance/backups/run", null)).StatusCode);
    }

    [Fact]
    public async Task Il_backup_è_cifrato_e_si_ripristina_su_un_server_nuovo()
    {
        Start();
        var admin = await InstanceAdminAsync();
        var project = (await (await admin.Client.PostAsJsonAsync($"/api/v1/orgs/{admin.OrgId}/projects", new { name = "Progetto da salvare" })).ReadJsonAsync())
            .GetProperty("id").GetGuid();
        Directory.CreateDirectory(Path.Combine(ReportsDirectory, "apple"));
        Directory.CreateDirectory(Path.Combine(ReportsDirectory, "_social"));
        await File.WriteAllTextAsync(Path.Combine(ReportsDirectory, "apple", "report.txt"), "report riconoscibile di Apple");
        await File.WriteAllTextAsync(Path.Combine(ReportsDirectory, "_social", "foto.jpg"), "file di un post");

        // Senza password non si parte; poi la programmazione dal pannello, controllata.
        Assert.Equal("backup_password_missing", await (await admin.Client.PostAsync("/api/v1/instance/backups/run", null)).ProblemCodeAsync());
        Assert.Equal("backup_password_missing", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup", new { active = "true" })).ProblemCodeAsync());
        Assert.Equal("backup_password", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup", new { password = "corta" })).ProblemCodeAsync());
        Assert.Equal("backup_timezone", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup", new { timeZone = "Marte/Olympus" })).ProblemCodeAsync());
        Assert.Equal("backup_time", await (await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup", new { time = "25:00" })).ProblemCodeAsync());
        var saved = await admin.Client.PutAsJsonAsync("/api/v1/instance/settings/backup",
            new { active = "true", time = "02:30", timeZone = "Europe/Rome", everyDays = "1", keep = "2", password = BackupPassword });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(BackupPassword, await saved.Content.ReadAsStringAsync());
        var before = await StatusAsync(admin);
        Assert.True(before.GetProperty("passwordSet").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, before.GetProperty("nextRunUtc").ValueKind);

        var status = await RunAndWaitAsync(admin);
        var run = status.GetProperty("runs")[0];
        Assert.True(run.GetProperty("error").ValueKind == JsonValueKind.Null, run.GetProperty("error").ToString());
        var name = Assert.Single(status.GetProperty("files").EnumerateArray()).GetProperty("name").GetString()!;
        var path = Path.Combine(_app.Root, "backups", name);

        // Nel file niente in chiaro: né la chiave master, né i report, né i dati.
        var file = await File.ReadAllBytesAsync(path);
        var masterKey = await File.ReadAllBytesAsync(Directory.GetFiles(KeysDirectory, "v1.key").Single());
        Assert.True(file.AsSpan().IndexOf(masterKey) < 0);
        Assert.True(file.AsSpan().IndexOf("report riconoscibile"u8) < 0);
        Assert.True(file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(admin.Email)) < 0);

        // Si scarica con un link a scadenza, uguale al file.
        var link = (await (await admin.Client.PostAsync($"/api/v1/instance/backups/files/{name}/link", null)).ReadJsonAsync()).GetProperty("url").GetString()!;
        Assert.Equal(file, await _app.CreateClient().GetByteArrayAsync(link));
        Assert.Equal(HttpStatusCode.NotFound, (await _app.CreateClient().GetAsync(link + "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsync("/api/v1/instance/backups/files/..%2F..%2Fkeys%2Fv1.key/link", null)).StatusCode);

        // Il ripristino, come su un server nuovo: database vuoto, cartelle vuote.
        var target = await postgres.CreateDatabaseAsync();
        var restorer = new BackupRestorer(new ContainerPgTools(postgres.ContainerId),
            Options.Create(new SecretsOptions { KeysDirectory = Path.Combine(_restoreRoot, "keys"), StorageDirectory = Path.Combine(_restoreRoot, "secrets") }),
            Options.Create(new ReportsOptions { StorageDirectory = Path.Combine(_restoreRoot, "reports") }));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => restorer.VerifyAsync(path, "password-sbagliata", CancellationToken.None));
        var summary = await restorer.RestoreAsync(path, BackupPassword, target, CancellationToken.None);
        Assert.True(summary.HasDatabase);
        Assert.Contains("keys", summary.Areas);

        Assert.Equal(masterKey, await File.ReadAllBytesAsync(Path.Combine(_restoreRoot, "keys", "v1.key")));
        Assert.Equal("report riconoscibile di Apple", await File.ReadAllTextAsync(Path.Combine(_restoreRoot, "reports", "apple", "report.txt")));
        Assert.False(File.Exists(Path.Combine(_restoreRoot, "reports", "_social", "foto.jpg"))); // i file dei post non ci sono

        await using (var connection = new NpgsqlConnection(target))
        {
            await connection.OpenAsync();
            async Task<long> CountAsync(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (long)(await command.ExecuteScalarAsync())!;
            }
            Assert.Equal(1, await CountAsync($"""SELECT count(*) FROM "User" WHERE "Email" = '{admin.Email}'"""));
            // La Row-Level Security c'è ancora: senza tenant il progetto non si vede, con il tenant sì.
            Assert.Equal(0, await CountAsync($"""SELECT count(*) FROM "Project" WHERE "Id" = '{project}'"""));
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{admin.OrgId}', false)", connection)) await set.ExecuteNonQueryAsync();
            Assert.Equal(1, await CountAsync($"""SELECT count(*) FROM "Project" WHERE "Id" = '{project}'"""));
            Assert.True(await CountAsync("""SELECT count(*) FROM "__EFMigrationsHistory" """) > 10);
        }

        // Se ne tengono 2: al terzo il primo se ne va.
        await RunAndWaitAsync(admin);
        var third = await RunAndWaitAsync(admin);
        var names = third.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(2, names.Count);
        Assert.DoesNotContain(name, names);
        Assert.Equal(3, third.GetProperty("runs").GetArrayLength());
    }

    [Fact]
    public async Task Il_worker_fa_il_backup_all_appuntamento_una_volta_sola()
    {
        Start(extra: new()
        {
            ["Backups:Active"] = "true", ["Backups:Time"] = "00:00", ["Backups:TimeZone"] = "UTC", ["Backups:Password"] = BackupPassword
        });
        await _app.SignUpAsync();
        var worker = new BackupWorker(_app.Services.GetRequiredService<IServiceScopeFactory>(), _app.Services.GetRequiredService<BackupRunner>(),
            _app.Services.GetRequiredService<IOptionsMonitor<BackupOptions>>(), NullLogger<BackupWorker>.Instance);

        var now = DateTime.UtcNow;
        Assert.True(await worker.RunOnceAsync(now, CancellationToken.None));
        Assert.False(await worker.RunOnceAsync(now.AddMinutes(1), CancellationToken.None));
        // Il giorno dopo, all'appuntamento nuovo, di nuovo.
        Assert.True(await worker.RunOnceAsync(now.Date.AddDays(1).AddMinutes(5), CancellationToken.None));

        using (var scope = _app.Services.CreateScope())
        {
            var runs = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<BackupRun>().ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, r => Assert.Null(r.Error));
            Assert.All(runs, r => Assert.False(r.Manual));
        }

        // Un backup rimasto aperto (l'app fermata a metà) all'avvio diventa "interrotto".
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            db.Add(BackupRun.Start(manual: true, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        await worker.CloseInterruptedAsync(CancellationToken.None);
        using (var scope = _app.Services.CreateScope())
        {
            var open = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Set<BackupRun>().SingleAsync(r => r.Manual);
            Assert.NotNull(open.FinishedAtUtc);
            Assert.StartsWith("Interrotto", open.Error);
        }
    }
}
