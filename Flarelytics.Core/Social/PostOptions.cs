using System.Text.Json;

namespace Flarelytics.Core.Social;

/// <summary>
/// Le scelte di un post che valgono solo per alcune reti. Stanno sul post (in
/// JSON), non sul singolo account: chi programma lo stesso video su due
/// account TikTok sceglie una volta.
/// </summary>
/// <param name="InstagramShowInGrid">
/// Reel: se compare anche nella griglia del profilo e nel feed
/// (<c>share_to_feed</c>). Falso = solo nella scheda Reel.
/// </param>
/// <param name="TikTokPrivacy">
/// Chi vede il video su TikTok: uno dei valori di <c>privacy_level_options</c>
/// del creator (PUBLIC_TO_EVERYONE, MUTUAL_FOLLOW_FRIENDS, FOLLOWER_OF_CREATOR,
/// SELF_ONLY). TikTok vuole che lo scelga la persona, senza un valore
/// predefinito: senza, il post non si programma.
/// </param>
/// <param name="TikTokBrandOrganic">Contenuto commerciale per il proprio marchio ("Contenuto promozionale").</param>
/// <param name="TikTokBrandedContent">Contenuto commerciale per terzi ("Partnership retribuita"): non può essere privato.</param>
public record PostOptions(
    bool InstagramShowInGrid = true,
    string? TikTokPrivacy = null,
    bool TikTokAllowComment = false,
    bool TikTokAllowDuet = false,
    bool TikTokAllowStitch = false,
    bool TikTokBrandOrganic = false,
    bool TikTokBrandedContent = false)
{
    public static readonly string[] TikTokPrivacyLevels = ["PUBLIC_TO_EVERYONE", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "SELF_ONLY"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PostOptions FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new PostOptions() : JsonSerializer.Deserialize<PostOptions>(json, Json) ?? new PostOptions();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
