namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Il cambio di riferimento della BCE: quante unità di <see cref="Currency"/>
/// vale un euro, in un giorno lavorativo.
/// </summary>
/// <remarks>
/// Dato pubblico e uguale per tutti, quindi non è del tenant. Nei fine
/// settimana e nei festivi la BCE non pubblica: per quei giorni vale l'ultimo
/// cambio precedente.
/// </remarks>
public class ExchangeRate
{
    public DateOnly Date { get; set; }
    public string Currency { get; set; } = null!;
    public decimal UnitsPerEuro { get; set; }
}
