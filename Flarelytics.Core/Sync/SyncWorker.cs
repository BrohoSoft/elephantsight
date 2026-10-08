using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Sync;

/// <summary>
/// Il giro di sincronizzazione, ripetuto ogni <see cref="SyncOptions.PollInterval"/>.
/// </summary>
/// <remarks>
/// Un giro alla volta: il successivo parte dopo la fine del precedente, non a
/// orario fisso, così un primo collegamento con un anno di storico non fa
/// partire un secondo giro sulle stesse credenziali. Per lo stesso motivo deve
/// girare in una sola istanza: lo ospita il processo dell'API, che in
/// un'installazione self-hosted è uno solo (<c>Worker:Enabled</c>).
/// </remarks>
public class SyncWorker(SyncCoordinator coordinator, IOptions<SyncOptions> options, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Worker avviato: un giro ogni {Interval}", options.Value.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Un giro andato male non deve fermare il worker: il prossimo
                // riprova, e intanto l'errore resta nel log.
                log.LogError(e, "Giro di sincronizzazione non riuscito");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
