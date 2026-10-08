namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Una riga per app, giorno e paese, con le metriche affiancate.
/// </summary>
/// <remarks>
/// <para><b>Legata all'app, non al progetto.</b> La chiave è lo store più l'id
/// con cui lo store conosce l'app (<see cref="AppId"/>, lo stesso di
/// <see cref="ProjectApp.ExternalAppId"/>). Così collegare o scollegare un'app
/// da un progetto non richiede di ricalcolare niente: il progetto legge le
/// righe delle sue app.</para>
///
/// <para>Niente <see cref="BaseEntity"/>: con decine di milioni di righe un
/// Guid in più per riga è spazio sprecato, e la chiave naturale c'è già.</para>
///
/// <para>Gli importi sono in micro-euro (milionesimi), interi: niente errori
/// di arrotondamento sommando milioni di righe. L'importo nella valuta
/// originale resta nel report grezzo.</para>
/// </remarks>
public class DailyAppMetric : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Store Store { get; set; }
    public string AppId { get; set; } = null!;
    public DateOnly Date { get; set; }

    /// <summary>ISO 3166 a due lettere, come lo scrive lo store.</summary>
    public string CountryCode { get; set; } = null!;

    /// <summary>
    /// Prime installazioni. Apple: "Free or paid app". Google: "Daily User
    /// Installs", cioè utenti che installano l'app per la prima volta su un
    /// qualunque dispositivo: è la definizione più vicina a quella di Apple.
    /// </summary>
    public int Downloads { get; set; }
    public int Redownloads { get; set; }
    public int Updates { get; set; }
    public int InAppPurchases { get; set; }

    /// <summary>Disinstallazioni. Solo Google Play: Apple non le comunica.</summary>
    public int Uninstalls { get; set; }

    /// <summary>Unità rimborsate, come numero positivo.</summary>
    public int Refunds { get; set; }

    /// <summary>Quanto incassa lo sviluppatore, al netto di commissioni e tasse, in micro-euro. I rimborsi lo riducono.</summary>
    public long ProceedsEurMicros { get; set; }

    /// <summary>Quanto ha pagato il cliente, tasse comprese, in micro-euro.</summary>
    public long SalesEurMicros { get; set; }

    /// <summary>
    /// Una parte degli importi era in una valuta per cui la BCE non pubblica un
    /// cambio, e non è inclusa nei totali in euro. Il frontend lo segnala.
    /// </summary>
    public bool HasUnconvertedAmounts { get; set; }
}
