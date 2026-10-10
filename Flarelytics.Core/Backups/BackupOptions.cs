namespace Flarelytics.Core.Backups;

/// <summary>
/// I backup dell'istanza (sezione <c>Backups</c>).
/// </summary>
/// <remarks>
/// <para><see cref="Enabled"/> si decide solo dall'ambiente (<c>BACKUPS_ENABLED</c>):
/// chi installa può spegnere del tutto la funzione, pannello compreso. È il caso
/// di un'istanza ospitata, dove i backup li fa chi la ospita, dalla sua
/// infrastruttura, e il cliente non deve poterli attivare.</para>
///
/// <para>Il resto (attivi o no, quando, ogni quanto, quanti tenerne, la
/// password) lo sceglie l'amministratore dell'istanza dal pannello: arriva
/// dalle impostazioni dell'istanza e si legge con <c>IOptionsMonitor</c>.</para>
/// </remarks>
public class BackupOptions
{
    public const string Section = "Backups";

    /// <summary>La funzione esiste: falso = niente pagina, niente rotte, niente backup.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dove si scrivono i file (un volume del server).</summary>
    public string? Directory { get; set; }

    /// <summary>I backup programmati sono accesi (dal pannello).</summary>
    public bool Active { get; set; }

    /// <summary>L'ora, <c>HH:mm</c>, nel fuso <see cref="TimeZone"/>.</summary>
    public string Time { get; set; } = "03:00";

    public string TimeZone { get; set; } = "Europe/Rome";

    /// <summary>Ogni quanti giorni.</summary>
    public int EveryDays { get; set; } = 1;

    /// <summary>Quanti file tenere: i più vecchi si cancellano dopo ogni backup riuscito.</summary>
    public int Keep { get; set; } = 14;

    /// <summary>
    /// La password che cifra i backup. Non la chiave master: un backup la
    /// contiene, e deve potersi aprire su un server nuovo dove la chiave non c'è.
    /// </summary>
    public string? Password { get; set; }

    public bool HasPassword => !string.IsNullOrEmpty(Password);

    public bool TryGetTime(out TimeOnly time) => TimeOnly.TryParseExact(Time?.Trim(), "HH:mm", out time);

    public TimeZoneInfo? FindTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone?.Trim() ?? "");
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }
}
