namespace Flarelytics.Core.Secrets;

/// <summary>Dove stanno le chiavi master e i file cifrati. Sezione <c>Secrets</c>.</summary>
public class SecretsOptions
{
    public const string Section = "Secrets";

    /// <summary>
    /// Cartella delle chiavi master: un file <c>&lt;versione&gt;.key</c> per
    /// ciascuna, con dentro 32 byte in base64. In produzione è un volume
    /// montato in sola lettura che <b>non</b> finisce nel backup dei dati.
    /// </summary>
    public string KeysDirectory { get; set; } = null!;

    /// <summary>La versione con cui si cifrano i file nuovi. Le altre restano per leggere quelli vecchi.</summary>
    public string ActiveKeyVersion { get; set; } = null!;

    /// <summary>Cartella dei file cifrati, uno per credenziale, in sottocartelle per tenant.</summary>
    public string StorageDirectory { get; set; } = null!;
}
