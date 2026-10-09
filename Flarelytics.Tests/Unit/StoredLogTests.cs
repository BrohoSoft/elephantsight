using Flarelytics.Core.Logging;
using Flarelytics.Core.Tenancy;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Tests.Unit;

public class StoredLogTests
{
    private static List<LogEntry> Drain(StoredLogProvider provider)
    {
        var list = new List<LogEntry>();
        while (provider.Reader.TryRead(out var e)) list.Add(e);
        return list;
    }

    [Fact]
    public void Dei_nostri_si_tengono_anche_gli_informativi_delle_librerie_solo_avvisi_ed_errori()
    {
        using var provider = new StoredLogProvider();
        provider.CreateLogger("Flarelytics.Core.Social.SocialPublisher").LogInformation("Post pubblicato su {Network}", "Threads");
        provider.CreateLogger("Flarelytics.Core.Social.SocialPublisher").LogDebug("dettaglio");
        provider.CreateLogger("System.Net.Http.HttpClient.ThreadsClient.ClientHandler").LogInformation("richiesta");
        provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").LogWarning("lento");
        // Il consiglio di EF sugli Include compare a ogni pagina: non si tiene.
        provider.CreateLogger("Microsoft.EntityFrameworkCore.Query").LogWarning(new EventId(20504), "più Include");

        var entries = Drain(provider);
        Assert.Equal(["Post pubblicato su Threads", "lento"], entries.Select(e => e.Message).ToArray());
        Assert.Equal(["Social", "Sistema"], entries.Select(e => e.Area).ToArray());
    }

    [Fact]
    public async Task Il_messaggio_prende_l_organizzazione_su_cui_si_sta_lavorando()
    {
        using var provider = new StoredLogProvider();
        var logger = provider.CreateLogger("Flarelytics.Core.Sync.AppleSalesSync");
        var tenant = Guid.NewGuid();

        logger.LogWarning("senza organizzazione");
        await Task.Run(() =>
        {
            new TenantContext().Set(tenant);
            logger.LogError(new InvalidOperationException("boom"), "con organizzazione");
        });

        var entries = Drain(provider);
        Assert.Null(entries[0].TenantId);
        Assert.Equal(tenant, entries[1].TenantId);
        Assert.Equal("Store", entries[1].Area);
        Assert.Contains("boom", entries[1].Exception);
    }

    [Fact]
    public void Un_messaggio_lunghissimo_si_tronca()
    {
        var entry = LogEntry.Create(DateTime.UtcNow, LogLevel.Error, "Flarelytics.X", new string('a', 10_000), new string('b', 20_000), null);
        Assert.Equal(LogEntry.MaxMessage, entry.Message.Length);
        Assert.Equal(LogEntry.MaxException, entry.Exception!.Length);
    }
}
