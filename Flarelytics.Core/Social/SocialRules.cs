using System.Globalization;
using System.Text.RegularExpressions;
using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Social;

/// <summary>
/// I limiti di una rete. Il pannello li riceve con gli account e mostra i
/// contatori mentre si scrive; il server li ricontrolla quando si programma un
/// post, così l'errore arriva subito e non all'ora della pubblicazione.
/// </summary>
/// <param name="CharacterCounting">Come conta la rete: <c>graphemes</c> (Bluesky), <c>codepoints</c>, <c>mastodon</c> (ogni link vale 23).</param>
/// <param name="Video"><c>none</c> (la rete non riceve video da ElephantSight), <c>optional</c> (Instagram: diventa un Reel), <c>required</c> (TikTok).</param>
/// <param name="RequiresFastStart">Il video deve avere l'indice in testa (Instagram, Reel).</param>
public record NetworkLimits(
    int MaxCharacters, int MaxImages, bool RequiresMedia, long MaxImageBytes,
    double? MinAspectRatio, double? MaxAspectRatio, int? MaxHashtags, string CharacterCounting,
    string Video = "none", long MaxVideoBytes = 0, int MinVideoSeconds = 0, int MaxVideoSeconds = 0, bool RequiresFastStart = false);

public static partial class SocialRules
{
    public static NetworkLimits For(SocialNetwork network, int? maxCharacters = null) => network switch
    {
        // app.bsky.feed.post: 300 grafemi; app.bsky.embed.images: 4 immagini da 1.000.000 byte.
        SocialNetwork.Bluesky => new(300, 4, false, 1_000_000, null, null, null, "graphemes"),
        // 500 è il valore di serie; l'istanza dice il suo quando si collega l'account.
        SocialNetwork.Mastodon => new(maxCharacters ?? 500, 4, false, 8 * 1024 * 1024, null, null, null, "mastodon"),
        // Didascalia 2200 caratteri e 30 hashtag; foto fra 4:5 e 1,91:1; caroselli fino a 10.
        // Un video diventa un Reel: da 3 secondi a 15 minuti, 300 MB, MP4/MOV con l'indice in testa.
        SocialNetwork.Instagram => new(2200, 10, true, 8 * 1024 * 1024, 0.8, 1.91, 30, "codepoints",
            Video: "optional", MaxVideoBytes: 300L * 1024 * 1024, MinVideoSeconds: 3, MaxVideoSeconds: 15 * 60, RequiresFastStart: true),
        SocialNetwork.FacebookPage => new(63206, 10, false, 10 * 1024 * 1024, null, null, null, "codepoints"),
        // Solo video (le foto vogliono un dominio verificato): titolo fino a 2200
        // caratteri, fino a 10 minuti (il creator può avere un limite più basso,
        // si controlla al momento di pubblicare), 4 GB.
        SocialNetwork.TikTok => new(2200, 1, true, 0, null, null, null, "codepoints",
            Video: "required", MaxVideoBytes: 4L * 1024 * 1024 * 1024, MinVideoSeconds: 1, MaxVideoSeconds: 10 * 60),
        _ => throw new ArgumentOutOfRangeException(nameof(network))
    };

    public static NetworkLimits For(SocialAccount account) => For(account.Network, account.MaxCharacters);

    public static int Count(string text, NetworkLimits limits) => limits.CharacterCounting switch
    {
        "graphemes" => new StringInfo(text).LengthInTextElements,
        // Mastodon conta ogni link come 23 caratteri, qualunque sia la sua lunghezza.
        "mastodon" => CodePoints(Url().Replace(text, new string('x', 23))),
        _ => CodePoints(text)
    };

    /// <summary>Quello che impedisce di pubblicare il testo e le immagini su quella rete, in frasi da mostrare.</summary>
    public static List<string> Problems(string text, IReadOnlyList<SocialMedia> media, NetworkLimits limits)
    {
        var problems = new List<string>();

        var count = Count(text, limits);
        if (count > limits.MaxCharacters) problems.Add($"il testo è di {count} caratteri, il massimo è {limits.MaxCharacters}");
        if (text.Length == 0 && media.Count == 0) problems.Add("il post è vuoto");
        var videos = media.Where(m => m.Kind == MediaKind.Video).ToList();
        if (videos.Count > 0)
        {
            if (limits.Video == "none") problems.Add("questa rete non riceve video da ElephantSight");
            else if (media.Count > 1) problems.Add("un video va pubblicato da solo, senza altre immagini");
            foreach (var v in videos)
            {
                var seconds = (v.DurationMs ?? 0) / 1000.0;
                if (limits.Video != "none" && v.SizeBytes > limits.MaxVideoBytes) problems.Add($"il video pesa {v.SizeBytes / 1024 / 1024} MB, il massimo è {limits.MaxVideoBytes / 1024 / 1024} MB");
                if (limits.Video != "none" && seconds < limits.MinVideoSeconds) problems.Add($"il video dura {seconds:0.#} secondi, il minimo è {limits.MinVideoSeconds}");
                if (limits.Video != "none" && seconds > limits.MaxVideoSeconds) problems.Add($"il video dura {seconds / 60:0.#} minuti, il massimo è {limits.MaxVideoSeconds / 60}");
                if (limits.RequiresFastStart && !v.FastStart) problems.Add("il video va esportato con l'indice in testa (\"faststart\", \"ottimizzato per il web\"), come vuole Instagram per i Reel");
            }
        }
        else
        {
            if (limits.Video == "required") problems.Add("serve un video");
            else if (limits.RequiresMedia && media.Count == 0) problems.Add("serve almeno un'immagine o un video");
            if (media.Count > limits.MaxImages) problems.Add($"al massimo {limits.MaxImages} immagini");
        }
        if (limits.MaxHashtags is { } maxTags && Hashtag().Matches(text).Count > maxTags) problems.Add($"al massimo {maxTags} hashtag");

        foreach (var (m, i) in media.Select((m, i) => (m, i + 1)).Where(x => x.m.Kind == MediaKind.Image))
        {
            if (m.SizeBytes > limits.MaxImageBytes) problems.Add($"l'immagine {i} pesa {m.SizeBytes / 1024} KB, il massimo è {limits.MaxImageBytes / 1024} KB");

            var ratio = (double)m.Width / m.Height;
            // Un margine dell'1%: un 1080×1350 arrotondato dal ridimensionamento resta un 4:5.
            if (limits.MinAspectRatio is { } min && ratio < min * 0.99 || limits.MaxAspectRatio is { } max && ratio > max * 1.01)
                problems.Add($"l'immagine {i} ha proporzioni {m.Width}×{m.Height}: servono fra 4:5 (verticale) e 1,91:1 (orizzontale)");
        }

        return problems;
    }

    /// <summary>
    /// Le scelte che una rete pretende prima di pubblicare. Per TikTok sono
    /// regole della piattaforma: chi vede il video lo sceglie la persona, e un
    /// contenuto per terzi ("partnership retribuita") non può essere privato.
    /// </summary>
    public static List<string> OptionProblems(SocialNetwork network, PostOptions options)
    {
        var problems = new List<string>();
        if (network != SocialNetwork.TikTok) return problems;

        if (options.TikTokPrivacy is null) problems.Add("scegli chi può vedere il video");
        else if (!PostOptions.TikTokPrivacyLevels.Contains(options.TikTokPrivacy)) problems.Add("la visibilità scelta non esiste su TikTok");
        if (options.TikTokBrandedContent && options.TikTokPrivacy == "SELF_ONLY")
            problems.Add("una partnership retribuita non può essere visibile solo a te");
        return problems;
    }

    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    [GeneratedRegex(@"https?://[^\s]+")]
    internal static partial Regex Url();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])#[\p{L}\p{N}_]+")]
    internal static partial Regex Hashtag();
}
