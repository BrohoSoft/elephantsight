namespace Flarelytics.Core.Social;

/// <summary>Configurazione della pubblicazione sui social (sezione <c>Social</c>).</summary>
public class SocialOptions
{
    public const string Section = "Social";

    /// <summary>
    /// L'indirizzo pubblico dell'istanza. Instagram e Facebook scaricano le
    /// immagini da qui, quindi deve essere raggiungibile da internet (con un
    /// tunnel Cloudflare va bene). Se manca si usa <c>Auth:PublicAppUrl</c>.
    /// </summary>
    public string? PublicUrl { get; set; }

    public MetaOptions Meta { get; set; } = new();

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
