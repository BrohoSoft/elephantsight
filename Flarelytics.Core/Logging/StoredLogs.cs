using System.Threading.Channels;
using Flarelytics.Core.Database;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Logging;

/// <summary>
/// Un messaggio del log, salvato a database perché si possa leggere dal
/// pannello (pagina Log) senza entrare nel server.
/// </summary>
/// <remarks>
/// Non è <c>ITenantOwned</c>: ci sono anche messaggi di sistema, di nessuna
/// organizzazione. Chi legge filtra per tenant a mano (vedi la rotta dei log).
/// </remarks>
public class LogEntry
{
    public long Id { get; private set; }
    public DateTime TimestampUtc { get; private set; }
    public LogLevel Level { get; private set; }

    /// <summary>Social, Store o Sistema: per filtrare senza conoscere le classi.</summary>
    public string Area { get; private set; } = "";

    public string Category { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? Exception { get; private set; }

    /// <summary>L'organizzazione a cui si riferisce (quella su cui lavorava chi ha scritto il messaggio); null = sistema.</summary>
    public Guid? TenantId { get; private set; }

    private LogEntry() { }

    public const int MaxMessage = 4000;
    public const int MaxException = 8000;

    public static LogEntry Create(DateTime timestampUtc, LogLevel level, string category, string message, string? exception, Guid? tenantId) => new()
    {
        TimestampUtc = timestampUtc, Level = level, Category = Trim(category, 200), Area = AreaOf(category),
        Message = Trim(message, MaxMessage), Exception = exception is null ? null : Trim(exception, MaxException), TenantId = tenantId
    };

    public static string AreaOf(string category) => category switch
    {
        _ when category.StartsWith("Flarelytics.Core.Social") || category.StartsWith("Flarelytics.Api.Features.Social") => "Social",
        _ when category.StartsWith("Flarelytics.Core.Sync") || category.StartsWith("Flarelytics.Core.Management")
               || category.StartsWith("Flarelytics.Core.Stores") || category.StartsWith("Flarelytics.Core.Reports") => "Store",
        _ => "Sistema"
    };

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

public class LogEntryConfiguration : IEntityTypeConfiguration<LogEntry>
{
    public void Configure(EntityTypeBuilder<LogEntry> b)
    {
        b.Property(e => e.Area).HasMaxLength(20);
        b.Property(e => e.Category).HasMaxLength(200);
        b.Property(e => e.Message).HasMaxLength(LogEntry.MaxMessage);
        b.Property(e => e.Exception).HasMaxLength(LogEntry.MaxException);

        // La pagina legge dal più recente, per organizzazione; la pulizia per data.
        b.HasIndex(e => new { e.TenantId, e.TimestampUtc });
        b.HasIndex(e => e.TimestampUtc);
    }
}

/// <summary>
/// Il provider di logging che mette i messaggi in coda per il database. Non
/// scrive niente lui: la scrittura la fa <see cref="StoredLogWriter"/>, a
/// blocchi, così un messaggio non aspetta mai il database.
/// </summary>
/// <remarks>
/// <para>Si tengono i messaggi di ElephantSight dall'informativo in su (cosa è
/// uscito, cosa è stato sincronizzato, cosa si riprova) e di tutto il resto
/// solo avvisi ed errori. L'organizzazione è quella di
/// <see cref="TenantContext.Ambient"/>: chi lavora su un tenant l'ha impostata.</para>
///
/// <para>Se la coda è piena (database fermo) i messaggi più vecchi si perdono:
/// restano comunque nel log del container.</para>
/// </remarks>
public sealed class StoredLogProvider : ILoggerProvider
{
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Vero mentre scrive il writer: i suoi messaggi (e quelli di EF per lui) non tornano in coda.</summary>
    internal static readonly AsyncLocal<bool> Writing = new();

    public ChannelReader<LogEntry> Reader => _queue.Reader;

    public ILogger CreateLogger(string categoryName) => new StoredLogger(categoryName, _queue.Writer);

    public void Dispose() => _queue.Writer.TryComplete();

    private sealed class StoredLogger(string category, ChannelWriter<LogEntry> queue) : ILogger
    {
        private readonly bool _ours = category.StartsWith("Flarelytics");

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= (_ours ? LogLevel.Information : LogLevel.Warning);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || Writing.Value) return;
            // "Più Include di collezioni in una query": un consiglio di EF che compare a ogni pagina, non un problema.
            if (eventId.Id == 20504 && category.StartsWith("Microsoft.EntityFrameworkCore")) return;

            var message = formatter(state, exception);
            // Al primo avvio EF cerca la tabella delle migration prima di crearla, e lo registra come errore.
            if (category == "Microsoft.EntityFrameworkCore.Database.Command" && message.Contains("__EFMigrationsHistory")) return;

            queue.TryWrite(LogEntry.Create(DateTime.UtcNow, level, category, message, exception?.ToString(), TenantContext.Ambient));
        }
    }
}

/// <summary>
/// Scrive a database i messaggi in coda, a blocchi di al massimo qualche
/// secondo, e una volta l'ora cancella quelli più vecchi di
/// <see cref="Retention"/>.
/// </summary>
public class StoredLogWriter(StoredLogProvider provider, IServiceScopeFactory scopes) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    private DateTime _lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StoredLogProvider.Writing.Value = true;
        var batch = new List<LogEntry>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await provider.Reader.WaitToReadAsync(stoppingToken)) break;
                // Un attimo per raccogliere gli altri messaggi dello stesso momento.
                await Task.Delay(FlushEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Allo spegnimento si scrive quello che è rimasto, poi si esce.
            }

            while (batch.Count < 500 && provider.Reader.TryRead(out var entry)) batch.Add(entry);
            await FlushAsync(batch);
            batch.Clear();
        }
    }

    /// <summary>Scrive (e ogni ora pulisce). Un errore qui non deve fermare l'app: i messaggi restano nel log del container.</summary>
    public async Task FlushAsync(IReadOnlyList<LogEntry> batch)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            if (batch.Count > 0)
            {
                db.AddRange(batch);
                await db.SaveChangesAsync();
            }

            if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
            {
                _lastCleanup = DateTime.UtcNow;
                var before = DateTime.UtcNow - Retention;
                await db.Set<LogEntry>().Where(e => e.TimestampUtc < before).ExecuteDeleteAsync();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Log non salvato a database ({batch.Count} messaggi): {e.Message}");
        }
    }
}

public static class StoredLogsExtensions
{
    /// <summary>I log anche a database, per la pagina Log del pannello.</summary>
    public static ILoggingBuilder AddStoredLogs(this ILoggingBuilder logging)
    {
        logging.Services.AddSingleton<StoredLogProvider>();
        logging.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<StoredLogProvider>());
        logging.Services.AddHostedService<StoredLogWriter>();
        return logging;
    }
}
