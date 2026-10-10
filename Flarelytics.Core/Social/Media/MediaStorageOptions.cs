namespace Flarelytics.Core.Social.Media;

/// <summary>Dove vanno i file nuovi dei post.</summary>
public enum MediaStorageMode
{
    /// <summary>Bunny se è configurato, altrimenti il disco (il predefinito).</summary>
    Auto = 0,

    /// <summary>Sempre il disco, anche se Bunny è configurato.</summary>
    Local = 1,

    /// <summary>
    /// Solo Bunny: il disco per i file dei post è spento. Senza Bunny
    /// configurato non si caricano immagini né video (i post di solo testo sì).
    /// </summary>
    Remote = 2
}

/// <summary>
/// I file dei post (sezione <c>Media</c>). La modalità si impone solo da
/// ambiente (<c>MEDIA_STORAGE</c>); Bunny si configura anche dalle
/// impostazioni dell'istanza, che vincono sul .env.
/// </summary>
public class MediaStorageOptions
{
    public const string Section = "Media";

    public MediaStorageMode Storage { get; set; } = MediaStorageMode.Auto;

    public BunnyStorageOptions Bunny { get; set; } = new();

    /// <summary>
    /// Dopo quanti giorni dalla pubblicazione (su tutti gli account) si
    /// cancellano gli originali: resta la miniatura. Prima no: Meta scarica il
    /// file al momento della pubblicazione, e "Riprova" ne ha bisogno.
    /// </summary>
    public int CleanupAfterDays { get; set; } = 7;
}

/// <summary>
/// Una storage zone di Bunny. Non serve una pull zone: i file non si servono
/// mai dalla CDN (sono cifrati), li legge e li decifra l'API.
/// </summary>
public class BunnyStorageOptions
{
    /// <summary>Le regioni dello Storage API e il loro host (documentazione di Bunny, "Storage endpoints").</summary>
    public static readonly IReadOnlyDictionary<string, string> Regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [""] = "storage.bunnycdn.com", // Falkenstein / Francoforte, il predefinito
        ["de"] = "storage.bunnycdn.com",
        ["uk"] = "uk.storage.bunnycdn.com",
        ["ny"] = "ny.storage.bunnycdn.com",
        ["la"] = "la.storage.bunnycdn.com",
        ["sg"] = "sg.storage.bunnycdn.com",
        ["se"] = "se.storage.bunnycdn.com",
        ["br"] = "br.storage.bunnycdn.com",
        ["jh"] = "jh.storage.bunnycdn.com",
        ["syd"] = "syd.storage.bunnycdn.com",
    };

    public string? StorageZone { get; set; }

    /// <summary>La regione principale della zone: vuoto (o <c>de</c>) è Falkenstein; poi uk, ny, la, sg, se, br, jh, syd.</summary>
    public string? Region { get; set; }

    /// <summary>La password della storage zone (scheda "FTP &amp; API Access"), non la chiave API dell'account.</summary>
    public string? AccessKey { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(StorageZone) && !string.IsNullOrWhiteSpace(AccessKey) && Regions.ContainsKey(Region?.Trim() ?? "");

    public string Host => Regions.TryGetValue(Region?.Trim() ?? "", out var host) ? host : Regions[""];
}
