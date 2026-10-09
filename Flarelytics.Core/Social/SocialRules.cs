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
public record NetworkLimits(
    int MaxCharacters, int MaxImages, bool RequiresMedia, long MaxImageBytes,
    double? MinAspectRatio, double? MaxAspectRatio, int? MaxHashtags, string CharacterCounting);

public static partial class SocialRules
{
    public static NetworkLimits For(SocialNetwork network, int? maxCharacters = null) => network switch
    {
        // app.bsky.feed.post: 300 grafemi; app.bsky.embed.images: 4 immagini da 1.000.000 byte.
        SocialNetwork.Bluesky => new(300, 4, false, 1_000_000, null, null, null, "graphemes"),
        // 500 è il valore di serie; l'istanza dice il suo quando si collega l'account.
        SocialNetwork.Mastodon => new(maxCharacters ?? 500, 4, false, 8 * 1024 * 1024, null, null, null, "mastodon"),
        // Didascalia 2200 caratteri e 30 hashtag; foto fra 4:5 e 1,91:1; caroselli fino a 10.
        SocialNetwork.Instagram => new(2200, 10, true, 8 * 1024 * 1024, 0.8, 1.91, 30, "codepoints"),
        SocialNetwork.FacebookPage => new(63206, 10, false, 10 * 1024 * 1024, null, null, null, "codepoints"),
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
        if (limits.RequiresMedia && media.Count == 0) problems.Add("serve almeno un'immagine");
        if (media.Count > limits.MaxImages) problems.Add($"al massimo {limits.MaxImages} immagini");
        if (limits.MaxHashtags is { } maxTags && Hashtag().Matches(text).Count > maxTags) problems.Add($"al massimo {maxTags} hashtag");

        foreach (var (m, i) in media.Select((m, i) => (m, i + 1)))
        {
            if (m.SizeBytes > limits.MaxImageBytes) problems.Add($"l'immagine {i} pesa {m.SizeBytes / 1024} KB, il massimo è {limits.MaxImageBytes / 1024} KB");

            var ratio = (double)m.Width / m.Height;
            // Un margine dell'1%: un 1080×1350 arrotondato dal ridimensionamento resta un 4:5.
            if (limits.MinAspectRatio is { } min && ratio < min * 0.99 || limits.MaxAspectRatio is { } max && ratio > max * 1.01)
                problems.Add($"l'immagine {i} ha proporzioni {m.Width}×{m.Height}: servono fra 4:5 (verticale) e 1,91:1 (orizzontale)");
        }

        return problems;
    }

    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    [GeneratedRegex(@"https?://[^\s]+")]
    internal static partial Regex Url();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])#[\p{L}\p{N}_]+")]
    internal static partial Regex Hashtag();
}
