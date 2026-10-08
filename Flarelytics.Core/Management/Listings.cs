using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Management;

/// <summary>I testi di una lingua su App Store. Null dove la scheda non ha quel campo.</summary>
/// <param name="InfoLocalizationId">La localizzazione di nome e sottotitolo (appInfoLocalizations).</param>
/// <param name="VersionLocalizationId">La localizzazione della versione (descrizione, parole chiave, novità…).</param>
public record AppleLocaleText(
    string Locale, string? InfoLocalizationId, string? Name, string? Subtitle, string? PrivacyPolicyUrl,
    string? VersionLocalizationId, string? Description, string? Keywords, string? WhatsNew, string? PromotionalText,
    string? MarketingUrl, string? SupportUrl);

/// <param name="InfoEditable">Nome e sottotitolo si modificano solo con la scheda dell'app in preparazione.</param>
/// <param name="VersionEditable">
/// Descrizione, parole chiave e novità solo su una versione in preparazione;
/// il testo promozionale invece sempre, anche sulla versione pubblicata.
/// </param>
public record AppleListing(string? Version, bool InfoEditable, bool VersionEditable, IReadOnlyList<AppleLocaleText> Locales);

public record GoogleLocaleText(string Language, string Title, string ShortDescription, string FullDescription, string? Video);

public record GoogleListing(string? DefaultLanguage, IReadOnlyList<GoogleLocaleText> Locales);

/// <summary>Uno screenshot: l'id sullo store e un indirizzo per vederlo.</summary>
public record ListingImage(string Id, string Url, string? FileName);

/// <param name="Group">Apple: il tipo di schermo (APP_IPHONE_67…). Google: il tipo d'immagine (phoneScreenshots…).</param>
public record ImageGroup(string Group, IReadOnlyList<ListingImage> Images);

/// <summary>
/// La pagina dello store: testi per lingua e screenshot, letti e modificati
/// sullo store.
/// </summary>
public class ListingService(AppleApi apple, GooglePublisher google, CredentialSecrets secrets)
{
    /// <summary>Gli stati in cui Apple lascia modificare i testi della versione.</summary>
    private static readonly HashSet<string> EditableVersionStates =
        ["PREPARE_FOR_SUBMISSION", "DEVELOPER_REJECTED", "REJECTED", "METADATA_REJECTED", "INVALID_BINARY"];

    private static readonly HashSet<string> EditableInfoStates =
        ["PREPARE_FOR_SUBMISSION", "DEVELOPER_REJECTED", "REJECTED", "READY_FOR_REVIEW"];

    public static readonly string[] GoogleImageTypes = ["phoneScreenshots", "sevenInchScreenshots", "tenInchScreenshots", "featureGraphic", "icon"];

    // ---- App Store -------------------------------------------------------

    public Task<AppleListing> GetAppleAsync(StoreCredential credential, string appId, CancellationToken ct) =>
        secrets.UseAsync(credential, secret => AppleListingAsync(apple.Open(credential, secret), appId, ct), ct);

    private static async Task<AppleListing> AppleListingAsync(AppleSession session, string appId, CancellationToken ct)
    {
        // Un'app ha fino a due "schede": quella pubblicata e, se si sta
        // preparando una modifica, quella nuova. Si mostra la nuova.
        var (infos, _) = await session.ListAsync($"v1/apps/{appId}/appInfos?fields[appInfos]=state,appStoreState", ct, maxPages: 1);
        var info = infos.FirstOrDefault(i => EditableInfoStates.Contains(i.Attr("state") ?? "")) ?? infos.FirstOrDefault();
        var infoEditable = info is not null && EditableInfoStates.Contains(info.Attr("state") ?? "");

        var (versions, _) = await session.ListAsync(
            $"v1/apps/{appId}/appStoreVersions?limit=10&fields[appStoreVersions]=versionString,appVersionState,appStoreState,createdDate", ct, maxPages: 1);
        string State(JsonNode v) => v.Attr("appVersionState") ?? v.Attr("appStoreState") ?? "";
        var version = versions.FirstOrDefault(v => EditableVersionStates.Contains(State(v)))
                      ?? versions.OrderByDescending(v => v.Attr("createdDate")).FirstOrDefault();
        var versionEditable = version is not null && EditableVersionStates.Contains(State(version));

        var infoLocales = info is null ? [] : (await session.ListAsync($"v1/appInfos/{info.Id()}/appInfoLocalizations?limit=50", ct)).Data;
        var versionLocales = version is null ? [] : (await session.ListAsync($"v1/appStoreVersions/{version.Id()}/appStoreVersionLocalizations?limit=50", ct)).Data;

        var locales = infoLocales.Select(l => l.Attr("locale")!).Union(versionLocales.Select(l => l.Attr("locale")!)).Order();

        return new AppleListing(version?.Attr("versionString"), infoEditable, versionEditable, locales.Select(locale =>
        {
            var i = infoLocales.FirstOrDefault(l => l.Attr("locale") == locale);
            var v = versionLocales.FirstOrDefault(l => l.Attr("locale") == locale);
            return new AppleLocaleText(locale,
                i?.Id(), i?.Attr("name"), i?.Attr("subtitle"), i?.Attr("privacyPolicyUrl"),
                v?.Id(), v?.Attr("description"), v?.Attr("keywords"), v?.Attr("whatsNew"), v?.Attr("promotionalText"),
                v?.Attr("marketingUrl"), v?.Attr("supportUrl"));
        }).ToList());
    }

    /// <summary>
    /// Salva i testi di una lingua. Si mandano solo i campi che lo stato della
    /// scheda permette; se l'utente ha cambiato un campo bloccato, si dice
    /// quale e perché invece di ignorarlo in silenzio.
    /// </summary>
    public Task UpdateAppleAsync(StoreCredential credential, string appId, AppleLocaleText text, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = apple.Open(credential, secret);
            var current = await AppleListingAsync(session, appId, ct);
            var locale = current.Locales.FirstOrDefault(l => l.Locale == text.Locale)
                ?? throw new StoreAccessException($"La lingua {text.Locale} non c'è nella scheda App Store: aggiungila da App Store Connect.");

            var infoChanged = text.Name != locale.Name || text.Subtitle != locale.Subtitle || text.PrivacyPolicyUrl != locale.PrivacyPolicyUrl;
            if (infoChanged)
            {
                if (!current.InfoEditable || locale.InfoLocalizationId is null)
                    throw new StoreAccessException("Nome, sottotitolo e privacy policy si cambiano solo quando la scheda dell'app è in preparazione: crea prima una nuova versione su App Store Connect.");

                await session.UpdateAsync("appInfoLocalizations", locale.InfoLocalizationId,
                    new { name = text.Name, subtitle = text.Subtitle, privacyPolicyUrl = text.PrivacyPolicyUrl }, ct);
            }

            if (locale.VersionLocalizationId is null) return true;

            var versionChanged = text.Description != locale.Description || text.Keywords != locale.Keywords || text.WhatsNew != locale.WhatsNew
                                 || text.MarketingUrl != locale.MarketingUrl || text.SupportUrl != locale.SupportUrl;
            if (versionChanged && !current.VersionEditable)
                throw new StoreAccessException("Descrizione, parole chiave e novità si cambiano solo su una versione in preparazione. Il testo promozionale invece si può cambiare sempre.");

            object attributes = current.VersionEditable
                ? new { description = text.Description, keywords = text.Keywords, whatsNew = text.WhatsNew, promotionalText = text.PromotionalText, marketingUrl = text.MarketingUrl, supportUrl = text.SupportUrl }
                : new { promotionalText = text.PromotionalText };

            await session.UpdateAsync("appStoreVersionLocalizations", locale.VersionLocalizationId, attributes, ct);
            return true;
        }, ct);

    public Task<IReadOnlyList<ImageGroup>> GetAppleScreenshotsAsync(StoreCredential credential, string appId, string locale, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = apple.Open(credential, secret);
            var localization = await AppleVersionLocalizationAsync(session, appId, locale, ct);
            return (IReadOnlyList<ImageGroup>)await AppleSetsAsync(session, localization, ct);
        }, ct);

    private static async Task<List<ImageGroup>> AppleSetsAsync(AppleSession session, string localizationId, CancellationToken ct)
    {
        var (sets, included) = await session.ListAsync(
            $"v1/appStoreVersionLocalizations/{localizationId}/appScreenshotSets?include=appScreenshots&limit[appScreenshots]=10" +
            "&fields[appScreenshots]=fileName,imageAsset,assetDeliveryState", ct);

        var shots = included.Where(i => i["type"]?.GetValue<string>() == "appScreenshots").ToDictionary(i => i.Id());

        return sets.Select(set => new ImageGroup(set.Attr("screenshotDisplayType") ?? "?",
            (set["relationships"]?["appScreenshots"]?["data"]?.AsArray().OfType<JsonNode>() ?? [])
                .Select(r => shots.GetValueOrDefault(r.Id()))
                .OfType<JsonNode>()
                .Select(s => new ListingImage(s.Id(), AppleImageUrl(s["attributes"]?["imageAsset"]), s.Attr("fileName")))
                .ToList())).ToList();
    }

    /// <summary>
    /// Carica uno screenshot nel gruppo indicato (creandolo se manca), con il
    /// flusso di Apple: si prenota l'asset, si manda il file come dicono le
    /// <c>uploadOperations</c>, e si conferma con l'MD5.
    /// </summary>
    public Task UploadAppleScreenshotAsync(StoreCredential credential, string appId, string locale, string displayType,
        string fileName, Stream file, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = apple.Open(credential, secret);
            var localization = await AppleVersionLocalizationAsync(session, appId, locale, ct);
            var (sets, _) = await session.ListAsync($"v1/appStoreVersionLocalizations/{localization}/appScreenshotSets", ct);

            var setId = sets.FirstOrDefault(s => s.Attr("screenshotDisplayType") == displayType)?.Id()
                ?? (await session.CreateAsync("appScreenshotSets", new { screenshotDisplayType = displayType },
                    new Dictionary<string, (string, string)> { ["appStoreVersionLocalization"] = ("appStoreVersionLocalizations", localization) }, ct))!["data"]!.Id();

            var reservation = await session.CreateAsync("appScreenshots", new { fileName, fileSize = file.Length },
                new Dictionary<string, (string, string)> { ["appScreenshotSet"] = ("appScreenshotSets", setId) }, ct);
            var screenshot = reservation!["data"]!;

            await session.UploadAsync(screenshot["attributes"]?["uploadOperations"], file, ct);

            file.Seek(0, SeekOrigin.Begin);
            var md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(file, ct));
            await session.UpdateAsync("appScreenshots", screenshot.Id(), new { uploaded = true, sourceFileChecksum = md5 }, ct);
            return true;
        }, ct);

    public Task DeleteAppleScreenshotAsync(StoreCredential credential, string screenshotId, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            await apple.Open(credential, secret).DeleteAsync("appScreenshots", screenshotId, ct);
            return true;
        }, ct);

    private static async Task<string> AppleVersionLocalizationAsync(AppleSession session, string appId, string locale, CancellationToken ct)
    {
        var listing = await AppleListingAsync(session, appId, ct);
        return listing.Locales.FirstOrDefault(l => l.Locale == locale)?.VersionLocalizationId
            ?? throw new StoreAccessException($"La versione non ha la lingua {locale}.");
    }

    /// <summary>
    /// Gli screenshot di Apple hanno un indirizzo modello
    /// (<c>…/{w}x{h}bb.{f}</c>): si chiede una miniatura alta 600 pixel.
    /// </summary>
    private static string AppleImageUrl(JsonNode? asset)
    {
        var template = asset?["templateUrl"]?.GetValue<string>() ?? "";
        var width = asset?["width"]?.GetValue<int>() ?? 1;
        var height = asset?["height"]?.GetValue<int>() ?? 1;
        var h = Math.Min(600, height);
        var w = Math.Max(1, (int)Math.Round((double)width * h / height));
        return template.Replace("{w}", w.ToString()).Replace("{h}", h.ToString()).Replace("{f}", "png");
    }

    // ---- Google Play -----------------------------------------------------

    public Task<GoogleListing> GetGoogleAsync(StoreCredential credential, string packageName, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = await google.OpenAsync(secret, ct);
            var app = GooglePublisher.App(packageName);
            return await session.WithEditAsync(packageName, commit: false, async edit =>
            {
                var details = await session.GetAsync($"{app}/edits/{edit}/details", ct);
                var listings = (await session.GetAsync($"{app}/edits/{edit}/listings", ct))?["listings"]?.AsArray().OfType<JsonNode>() ?? [];

                return new GoogleListing(details?["defaultLanguage"]?.GetValue<string>(), listings
                    .Select(l => new GoogleLocaleText(l["language"]!.GetValue<string>(), l["title"]?.GetValue<string>() ?? "",
                        l["shortDescription"]?.GetValue<string>() ?? "", l["fullDescription"]?.GetValue<string>() ?? "", l["video"]?.GetValue<string>()))
                    .OrderBy(l => l.Language).ToList());
            }, ct);
        }, ct);

    /// <summary>Salva i testi di una lingua e conferma l'edit: la modifica va in revisione su Google Play.</summary>
    public Task UpdateGoogleAsync(StoreCredential credential, string packageName, GoogleLocaleText text, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = await google.OpenAsync(secret, ct);
            var app = GooglePublisher.App(packageName);
            return await session.WithEditAsync(packageName, commit: true, async edit =>
            {
                await session.SendJsonAsync(HttpMethod.Put, $"{app}/edits/{edit}/listings/{Uri.EscapeDataString(text.Language)}", new
                {
                    language = text.Language, title = text.Title, shortDescription = text.ShortDescription,
                    fullDescription = text.FullDescription, video = text.Video
                }, ct);
                return true;
            }, ct);
        }, ct);

    public Task<IReadOnlyList<ImageGroup>> GetGoogleImagesAsync(StoreCredential credential, string packageName, string language, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = await google.OpenAsync(secret, ct);
            var app = GooglePublisher.App(packageName);
            return await session.WithEditAsync(packageName, commit: false, async edit =>
            {
                var groups = new List<ImageGroup>();
                foreach (var type in GoogleImageTypes)
                {
                    var images = (await session.GetAsync($"{app}/edits/{edit}/listings/{Uri.EscapeDataString(language)}/{type}", ct))?["images"]?.AsArray().OfType<JsonNode>() ?? [];
                    groups.Add(new ImageGroup(type, images.Select(i => new ListingImage(i["id"]!.GetValue<string>(), i["url"]!.GetValue<string>() + "=h600", null)).ToList()));
                }
                return (IReadOnlyList<ImageGroup>)groups;
            }, ct);
        }, ct);

    public Task UploadGoogleImageAsync(StoreCredential credential, string packageName, string language, string imageType,
        string contentType, Stream file, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = await google.OpenAsync(secret, ct);
            return await session.WithEditAsync(packageName, commit: true, async edit =>
            {
                await session.UploadAsync($"{GooglePublisher.UploadApp(packageName)}/edits/{edit}/listings/{Uri.EscapeDataString(language)}/{imageType}?uploadType=media",
                    file, contentType, ct);
                return true;
            }, ct);
        }, ct);

    public Task DeleteGoogleImageAsync(StoreCredential credential, string packageName, string language, string imageType, string imageId, CancellationToken ct) =>
        secrets.UseAsync(credential, async secret =>
        {
            var session = await google.OpenAsync(secret, ct);
            return await session.WithEditAsync(packageName, commit: true, async edit =>
            {
                await session.DeleteAsync($"{GooglePublisher.App(packageName)}/edits/{edit}/listings/{Uri.EscapeDataString(language)}/{imageType}/{Uri.EscapeDataString(imageId)}", ct);
                return true;
            }, ct);
        }, ct);
}
