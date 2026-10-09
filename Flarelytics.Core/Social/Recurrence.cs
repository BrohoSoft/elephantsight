using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Social;

/// <summary>
/// Quando esce un post ricorrente. Le date si calcolano nel fuso di chi l'ha
/// programmato e poi si portano in UTC: "ogni giorno alle 9" resta alle 9 di
/// Roma anche quando cambia l'ora legale.
/// </summary>
/// <remarks>
/// <para>Le uscite sono una griglia che parte da <see cref="StartDate"/>:
/// giornaliero = un giorno ogni <see cref="Interval"/>; settimanale = i giorni
/// scelti, una settimana (da lunedì) ogni <see cref="Interval"/>; mensile = il
/// giorno del mese di <see cref="StartDate"/>, un mese ogni
/// <see cref="Interval"/> (il 31 diventa l'ultimo giorno nei mesi più corti).</para>
///
/// <para>Ora legale: un'ora che non esiste (le 2:30 del giorno in cui si va
/// avanti) diventa un'ora dopo; un'ora che c'è due volte vale la prima.</para>
/// </remarks>
public record RecurrenceRule(
    RecurrenceFrequency Frequency, int Interval, int DaysOfWeek, TimeOnly TimeOfDay, string TimeZone, DateOnly StartDate, DateOnly? EndDate)
{
    /// <summary>Lunedì per primo: è l'ordine della settimana in Italia, e quello in cui si scorrono i giorni.</summary>
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static int Bit(DayOfWeek day) => 1 << (int)day;

    public static bool IsValidTimeZone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>Un fuso sparito dal sistema (non dovrebbe succedere) vale UTC invece di fermare il worker.</summary>
    private TimeZoneInfo Zone => TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var tz) ? tz : TimeZoneInfo.Utc;

    /// <summary>La prima uscita dopo <paramref name="afterUtc"/> (esclusa), o null se la serie è finita.</summary>
    public DateTime? NextAfter(DateTime afterUtc)
    {
        var zone = Zone;
        // Un giorno prima: un'uscita alle 23 del giorno prima, in un fuso
        // indietro rispetto a UTC, cade già nel giorno dopo in UTC.
        var from = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone)).AddDays(-1);
        if (from < StartDate) from = StartDate;

        // Le date della griglia sono in ordine: la prima dopo l'istante è la risposta.
        foreach (var date in DatesFrom(from).Take(10_000))
        {
            if (EndDate is { } end && date > end) return null;
            var utc = ToUtc(date, zone);
            if (utc > afterUtc) return utc;
        }
        return null;
    }

    /// <summary>Le uscite fra due istanti (il primo compreso, il secondo no), al massimo <paramref name="max"/>.</summary>
    public List<DateTime> Between(DateTime fromUtc, DateTime toUtc, int max = 500)
    {
        var list = new List<DateTime>();
        var next = NextAfter(fromUtc.AddTicks(-1));
        while (next is { } at && at < toUtc && list.Count < max)
        {
            list.Add(at);
            next = NextAfter(at);
        }
        return list;
    }

    /// <summary>Le date della griglia da <paramref name="from"/> in avanti, in ordine.</summary>
    private IEnumerable<DateOnly> DatesFrom(DateOnly from)
    {
        var interval = Math.Max(1, Interval);
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
            {
                var steps = (from.DayNumber - StartDate.DayNumber + interval - 1) / interval;
                for (var date = StartDate.AddDays(steps * interval); ; date = date.AddDays(interval)) yield return date;
            }
            case RecurrenceFrequency.Weekly:
            {
                if ((DaysOfWeek & 0x7F) == 0) yield break;
                var firstMonday = Monday(StartDate);
                var week = (Monday(from).DayNumber - firstMonday.DayNumber) / 7;
                if (week % interval != 0) week += interval - week % interval; // le settimane fuori dalla griglia non hanno uscite
                for (; ; week += interval)
                {
                    foreach (var (day, i) in Week.Select((d, i) => (d, i)))
                    {
                        var date = firstMonday.AddDays(week * 7 + i);
                        if ((DaysOfWeek & Bit(day)) != 0 && date >= from && date >= StartDate) yield return date;
                    }
                }
            }
            case RecurrenceFrequency.Monthly:
            {
                var months = Math.Max(0, (from.Year * 12 + from.Month) - (StartDate.Year * 12 + StartDate.Month));
                if (months % interval != 0) months += interval - months % interval;
                for (; ; months += interval)
                {
                    var first = new DateOnly(StartDate.Year, StartDate.Month, 1).AddMonths(months);
                    var date = new DateOnly(first.Year, first.Month, Math.Min(StartDate.Day, DateTime.DaysInMonth(first.Year, first.Month)));
                    if (date >= from) yield return date;
                }
            }
        }
    }

    private static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private DateTime ToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOfDay, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local))
        {
            // Le due letture dell'ora ripetuta: la prima è quella con l'offset più grande.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
