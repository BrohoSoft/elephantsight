namespace Flarelytics.Core.Backups;

/// <summary>
/// Quando tocca a un backup programmato: all'ora scelta, nel fuso scelto, ogni
/// N giorni contati da una data fissa (così "ogni 3 giorni" non scivola se un
/// giro salta o se il server si riavvia).
/// </summary>
public static class BackupSchedule
{
    private static readonly DateOnly Anchor = new(2026, 1, 1);

    /// <summary>L'ultimo appuntamento già arrivato (null se la regola non è valida).</summary>
    public static DateTime? LatestSlotUtc(BackupOptions options, DateTime nowUtc)
    {
        if (!options.TryGetTime(out var time) || options.FindTimeZone() is not { } zone) return null;
        var every = Math.Clamp(options.EveryDays, 1, 365);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var day = today; day >= today.AddDays(-every - 1); day = day.AddDays(-1))
        {
            if ((day.DayNumber - Anchor.DayNumber) % every != 0) continue;
            var slot = SlotUtc(day, time, zone);
            if (slot <= nowUtc) return slot;
        }
        return null;
    }

    /// <summary>Il prossimo appuntamento dopo adesso, per mostrarlo nel pannello.</summary>
    public static DateTime? NextSlotUtc(BackupOptions options, DateTime nowUtc)
    {
        if (!options.TryGetTime(out var time) || options.FindTimeZone() is not { } zone) return null;
        var every = Math.Clamp(options.EveryDays, 1, 365);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var day = today; day <= today.AddDays(every + 1); day = day.AddDays(1))
        {
            if ((day.DayNumber - Anchor.DayNumber) % every != 0) continue;
            var slot = SlotUtc(day, time, zone);
            if (slot > nowUtc) return slot;
        }
        return null;
    }

    private static DateTime SlotUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        // Un'ora che non esiste (il cambio all'ora legale) slitta avanti di un'ora.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
