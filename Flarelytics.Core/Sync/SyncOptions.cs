namespace Flarelytics.Core.Sync;

/// <summary>Sezione <c>Sync</c>.</summary>
public class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>Ogni quanto una credenziale si risincronizza da sola. "Sincronizza ora" la anticipa.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Ogni quanto il worker controlla se c'è qualcosa da fare.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Pausa fra due richieste allo stesso store. Al primo collegamento si
    /// scaricano 365 giorni: senza pausa sarebbero 365 richieste di fila, e
    /// Apple risponderebbe 429.
    /// </summary>
    public TimeSpan RequestDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Quanti giorni di storico si recuperano. Apple tiene i report giornalieri per circa un anno.</summary>
    public int BackfillDays { get; set; } = 365;

    /// <summary>
    /// I giorni recenti trovati vuoti si richiedono di nuovo: Apple risponde
    /// 404 anche quando il report non è ancora pronto.
    /// </summary>
    public int RecentDaysToRetry { get; set; } = 3;
}

/// <summary>Il calendario di Apple: i report giornalieri seguono il giorno del Pacifico, non il nostro.</summary>
public static class AppleCalendar
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public static DateOnly Today(DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, Pacific));
}
