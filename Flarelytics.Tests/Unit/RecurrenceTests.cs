using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;

namespace Flarelytics.Tests.Unit;

public class RecurrenceTests
{
    private const string Rome = "Europe/Rome";

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static RecurrenceRule Daily(int interval = 1, string time = "09:00", DateOnly? start = null, DateOnly? end = null) =>
        new(RecurrenceFrequency.Daily, interval, 0, TimeOnly.Parse(time), Rome, start ?? new DateOnly(2026, 10, 1), end);

    [Fact]
    public void Ogni_giorno_alle_9_resta_alle_9_di_Roma_quando_finisce_l_ora_legale()
    {
        // Il 25 ottobre 2026 Roma torna da UTC+2 a UTC+1.
        var rule = Daily();
        Assert.Equal(Utc(2026, 10, 24, 7), rule.NextAfter(Utc(2026, 10, 23, 8)));
        Assert.Equal(Utc(2026, 10, 25, 8), rule.NextAfter(Utc(2026, 10, 24, 7)));
        Assert.Equal(Utc(2026, 10, 26, 8), rule.NextAfter(Utc(2026, 10, 25, 8)));
    }

    [Fact]
    public void La_prima_uscita_non_viene_prima_dell_inizio_e_l_istante_dato_e_escluso()
    {
        var rule = Daily(start: new DateOnly(2026, 11, 10));
        Assert.Equal(Utc(2026, 11, 10, 8), rule.NextAfter(Utc(2026, 10, 1, 0)));
        Assert.Equal(Utc(2026, 11, 11, 8), rule.NextAfter(Utc(2026, 11, 10, 8)));
    }

    [Fact]
    public void Ogni_tre_giorni_segue_la_griglia_che_parte_dall_inizio()
    {
        var rule = Daily(interval: 3, start: new DateOnly(2026, 10, 1));
        // 1, 4, 7, 10…: dal 5 la prossima è il 7.
        Assert.Equal(Utc(2026, 10, 7, 7), rule.NextAfter(Utc(2026, 10, 5, 12)));
    }

    [Fact]
    public void La_fine_e_compresa_e_poi_la_serie_finisce()
    {
        var rule = Daily(end: new DateOnly(2026, 10, 3));
        Assert.Equal(Utc(2026, 10, 3, 7), rule.NextAfter(Utc(2026, 10, 2, 7)));
        Assert.Null(rule.NextAfter(Utc(2026, 10, 3, 7)));
    }

    [Fact]
    public void Settimanale_nei_giorni_scelti_una_settimana_si_e_una_no()
    {
        // Lunedì e giovedì, ogni due settimane, dalla settimana di giovedì 1 ottobre 2026.
        var days = RecurrenceRule.Bit(DayOfWeek.Monday) | RecurrenceRule.Bit(DayOfWeek.Thursday);
        var rule = new RecurrenceRule(RecurrenceFrequency.Weekly, 2, days, new TimeOnly(18, 30), Rome, new DateOnly(2026, 10, 1), null);

        var dates = rule.Between(Utc(2026, 9, 1, 0), Utc(2026, 11, 1, 0)).Select(d => DateOnly.FromDateTime(d)).ToList();
        // Il lunedì 28 settembre è prima dell'inizio; la settimana del 5 ottobre si salta.
        Assert.Equal([new(2026, 10, 1), new(2026, 10, 12), new(2026, 10, 15), new(2026, 10, 26), new(2026, 10, 29)], dates);
    }

    [Fact]
    public void Mensile_il_31_diventa_l_ultimo_giorno_nei_mesi_piu_corti()
    {
        var rule = new RecurrenceRule(RecurrenceFrequency.Monthly, 1, 0, new TimeOnly(12, 0), "UTC", new DateOnly(2026, 12, 31), null);
        var dates = rule.Between(Utc(2026, 12, 1, 0), Utc(2027, 5, 1, 0)).Select(d => DateOnly.FromDateTime(d)).ToList();
        Assert.Equal([new(2026, 12, 31), new(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31), new(2027, 4, 30)], dates);
    }

    [Fact]
    public void Ogni_tre_mesi_dal_mese_di_inizio()
    {
        var rule = new RecurrenceRule(RecurrenceFrequency.Monthly, 3, 0, new TimeOnly(10, 0), "UTC", new DateOnly(2026, 1, 15), null);
        Assert.Equal(Utc(2026, 7, 15, 10), rule.NextAfter(Utc(2026, 4, 16, 0)));
    }

    [Fact]
    public void Un_ora_che_non_esiste_per_l_ora_legale_diventa_un_ora_dopo()
    {
        // Il 29 marzo 2026 a Roma dalle 2:00 si passa alle 3:00: le 2:30 non esistono.
        var rule = Daily(time: "02:30", start: new DateOnly(2026, 3, 28));
        Assert.Equal(Utc(2026, 3, 29, 1, 30), rule.NextAfter(Utc(2026, 3, 28, 12)));
    }

    [Fact]
    public void Un_ora_ripetuta_vale_la_prima_volta()
    {
        // Il 25 ottobre 2026 a Roma le 2:30 ci sono due volte: alle 0:30 e alle 1:30 UTC.
        var rule = Daily(time: "02:30");
        Assert.Equal(Utc(2026, 10, 25, 0, 30), rule.NextAfter(Utc(2026, 10, 24, 12)));
    }

    [Fact]
    public void Un_uscita_serale_in_un_fuso_indietro_rispetto_a_UTC_cade_il_giorno_dopo_in_UTC()
    {
        var rule = new RecurrenceRule(RecurrenceFrequency.Daily, 1, 0, new TimeOnly(22, 0), "America/New_York", new DateOnly(2026, 10, 1), null);
        // Le 22 di New York del 5 ottobre (UTC-4) sono le 2 del 6 in UTC.
        Assert.Equal(Utc(2026, 10, 6, 2), rule.NextAfter(Utc(2026, 10, 5, 12)));
    }
}
