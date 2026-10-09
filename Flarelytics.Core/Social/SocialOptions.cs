namespace Flarelytics.Core.Social;

/// <summary>Configurazione della pubblicazione sui social (sezione <c>Social</c>).</summary>
public class SocialOptions
{
    public const string Section = "Social";

    /// <summary>
    /// L'indirizzo da cui Instagram e Facebook scaricano le immagini: deve
    /// essere raggiungibile da internet (con un tunnel Cloudflare va bene). Se
    /// manca si usa <c>Auth:PublicAppUrl</c>. In sviluppo può essere un quick
    /// tunnel verso l'API, mentre il pannello resta su localhost.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// L'indirizzo del pannello (<c>Auth:PublicAppUrl</c>), dove tornano i
    /// login di Facebook e Instagram: lì il browser ha la sessione.
    /// </summary>
    public string AppUrl { get; set; } = "";

    public MetaOptions Meta { get; set; } = new();

    public InstagramOptions Instagram { get; set; } = new();

    public TikTokOptions TikTok { get; set; } = new();

    /// <summary>Quanto aspettare fra un controllo e l'altro di un'elaborazione (Instagram, Mastodon). Zero nei test.</summary>
    public TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// L'app Meta di chi installa: serve per Instagram e le Pagine Facebook. Si
/// crea su developers.facebook.com, una volta per istanza.
/// </summary>
public class MetaOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string GraphVersion { get; set; } = "v26.0";

    public bool Enabled => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>
/// Instagram con "Instagram Login": per gli account professionali che non
/// hanno una Pagina Facebook. Le credenziali sono l'Instagram App ID e il suo
/// secret (prodotto Instagram dell'app Meta → Business login), non quelli
/// dell'app Meta.
/// </summary>
public class InstagramOptions
{
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>L'app TikTok di chi installa (developers.tiktok.com): Login Kit e Content Posting API.</summary>
public class TikTokOptions
{
    public string? ClientKey { get; set; }
    public string? ClientSecret { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientKey) && !string.IsNullOrWhiteSpace(ClientSecret);
}
