using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Sync;

/// <summary>
/// Un giro del worker: cambi, poi tutte le credenziali da sincronizzare, tenant
/// per tenant.
/// </summary>
/// <remarks>
/// <para><b>Uno scope per tenant.</b> Il worker non ha un tenant suo: scorre la
/// tabella dei tenant (che non è filtrata) e per ciascuno apre uno scope nuovo
/// e imposta <see cref="TenantContext"/>. Da lì in poi vale tutto come
/// nell'API, Row-Level Security compresa: un errore nel codice di
/// sincronizzazione non può scrivere metriche nel tenant sbagliato.</para>
///
/// </remarks>
public class SyncCoordinator(IServiceScopeFactory scopes, IOptions<SyncOptions> options, ILogger<SyncCoordinator> log)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<RatesRefresher>().RefreshAsync(now, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Senza cambi nuovi i download vanno avanti lo stesso: i report
            // aspettano a essere elaborati, non a essere scaricati.
            log.LogWarning(e, "Aggiornamento dei cambi BCE non riuscito");
        }

        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>()
                .Set<Tenant>().Select(t => t.Id).ToListAsync(ct);
        }

        foreach (var tenantId in tenants)
        {
            await SyncTenantAsync(tenantId, now, ct);
        }
    }

    public async Task SyncTenantAsync(Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<TenantContext>().Set(tenantId);
        var db = services.GetRequiredService<FlarelyticsDbContext>();

        // Le icone prima dei report: sono poche richieste, e il progetto
        // appena creato ha subito la sua faccia mentre lo storico scarica.
        try
        {
            await services.GetRequiredService<IconRefresher>().RefreshAsync(nowUtc, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Aggiornamento delle icone non riuscito per il tenant {Tenant}", tenantId);
            db.ChangeTracker.Clear();
        }

        var dueBefore = nowUtc - options.Value.Interval;
        var credentials = await db.Set<StoreCredential>()
            .Where(c => c.Status != CredentialStatus.Invalid
                        && (c.SyncRequestedAtUtc != null || c.LastSyncCompletedAtUtc == null || c.LastSyncCompletedAtUtc < dueBefore))
            .ToListAsync(ct);

        foreach (var credential in credentials)
        {
            try
            {
                _ = credential.Store switch
                {
                    Store.AppStore => await services.GetRequiredService<AppleSalesSync>().SyncAsync(credential, nowUtc, ct),
                    Store.GooglePlay => await services.GetRequiredService<GooglePlaySync>().SyncAsync(credential, nowUtc, ct),
                    _ => throw new InvalidOperationException($"Nessuna sincronizzazione per {credential.Store}.")
                };

                // Le recensioni delle app collegate con questa chiave, allo
                // stesso ritmo dei download. Ogni app ha il suo stato e il suo
                // errore: una chiave senza il permesso per le recensioni non
                // ferma il resto.
                var apps = await db.Set<ProjectApp>().Where(a => a.CredentialId == credential.Id)
                    .Select(a => a.ExternalAppId).Distinct().ToListAsync(ct);
                foreach (var appId in apps)
                {
                    await services.GetRequiredService<Management.ReviewsService>().SyncAsync(credential, appId, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Sincronizzazione della credenziale {Credential} non riuscita", credential.Id);
                db.ChangeTracker.Clear();
                db.Attach(credential);
                credential.RecordSync(DateTime.UtcNow, "Errore interno durante la sincronizzazione: riproveremo al prossimo giro.");
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
