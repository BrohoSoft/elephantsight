using System.Text.Json.Nodes;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;

namespace Flarelytics.Core.Management;

/// <summary>Lo stato di una versione o di una build, ridotto a poche categorie comuni ai due store.</summary>
public enum ReleaseStage
{
    /// <summary>Pubblicata e scaricabile (Apple READY_FOR_SALE, Google completed in produzione).</summary>
    Live,

    /// <summary>In distribuzione graduale o in un canale di test.</summary>
    Rolling,

    /// <summary>Inviata e in attesa: revisione, approvata ma non ancora rilasciata.</summary>
    InReview,

    /// <summary>In preparazione: bozza, non ancora inviata.</summary>
    Draft,

    Rejected,

    /// <summary>Superata da una versione più recente, o interrotta.</summary>
    Retired,

    /// <summary>Build in elaborazione dopo il caricamento.</summary>
    Processing,

    Other
}

/// <param name="Version">Il numero visibile (1.4.0). Per Google è il nome della release, se c'è.</param>
/// <param name="Track">Solo Google: production, beta, alpha, internal o un canale personalizzato.</param>
/// <param name="RawState">Lo stato come lo scrive lo store, per chi vuole il dettaglio.</param>
/// <param name="RolloutPercent">Solo Google, in distribuzione graduale.</param>
public record VersionInfo(string Version, ReleaseStage Stage, string RawState, string? Track, DateTime? CreatedAtUtc, double? RolloutPercent, IReadOnlyList<string> BuildNumbers, string? ReleaseNotes);

/// <param name="Version">Il numero visibile della build (Apple), se lo store lo dà.</param>
/// <param name="BuildNumber">CFBundleVersion per Apple, versionCode per Google.</param>
/// <param name="Tracks">Solo Google: i canali in cui la build è usata.</param>
public record BuildInfo(string? Version, string BuildNumber, ReleaseStage Stage, string RawState, DateTime? UploadedAtUtc, IReadOnlyList<string> Tracks);

/// <param name="Error">Se lo store non ha risposto o la chiave non può leggere: il resto della pagina si vede lo stesso.</param>
public record StoreReleases(Store Store, string AppId, IReadOnlyList<VersionInfo> Versions, IReadOnlyList<BuildInfo> Builds, string? Error = null);

/// <summary>Versioni e build di un'app, lette ogni volta dallo store: sono dati che cambiano di ora in ora.</summary>
public class ReleasesService(AppleApi apple, GooglePublisher google, CredentialSecrets secrets)
{
    public async Task<StoreReleases> GetAsync(StoreCredential credential, string appId, CancellationToken ct)
    {
        try
        {
            return await secrets.UseAsync(credential, secret => credential.Store == Store.AppStore
                ? AppleAsync(apple.Open(credential, secret), appId, ct)
                : GoogleAsync(secret, appId, ct), ct);
        }
        catch (StoreAccessException e)
        {
            return new StoreReleases(credential.Store, appId, [], [], e.Message);
        }
        catch (HttpRequestException)
        {
            return new StoreReleases(credential.Store, appId, [], [], "Lo store non risponde: riprova fra poco.");
        }
    }

    private static async Task<StoreReleases> AppleAsync(AppleSession session, string appId, CancellationToken ct)
    {
        var (versions, _) = await session.ListAsync(
            $"v1/apps/{appId}/appStoreVersions?limit=20&fields[appStoreVersions]=versionString,appVersionState,appStoreState,platform,createdDate,releaseType,earliestReleaseDate",
            ct, maxPages: 1);

        var (builds, included) = await session.ListAsync(
            $"v1/builds?filter[app]={appId}&sort=-uploadedDate&limit=30&fields[builds]=version,uploadedDate,processingState,expired,preReleaseVersion&include=preReleaseVersion&fields[preReleaseVersions]=version,platform",
            ct, maxPages: 1);

        var marketing = included.Where(i => i["type"]?.GetValue<string>() == "preReleaseVersions").ToDictionary(i => i.Id(), i => i.Attr("version"));

        return new StoreReleases(Store.AppStore, appId,
            versions
                .Select(v =>
                {
                    // appVersionState è il campo nuovo; appStoreState quello storico, ancora presente.
                    var state = v.Attr("appVersionState") ?? v.Attr("appStoreState") ?? "";
                    return new VersionInfo(v.Attr("versionString") ?? "?", AppleStage(state), state, v.Attr("platform"),
                        DateTime.TryParse(v.Attr("createdDate"), out var created) ? created.ToUniversalTime() : null, null, [], null);
                })
                .OrderByDescending(v => v.CreatedAtUtc)
                .ToList(),
            builds
                .Select(b =>
                {
                    var expired = b.Attr<bool>("expired");
                    var state = expired ? "EXPIRED" : b.Attr("processingState") ?? "";
                    return new BuildInfo(
                        b.RelatedId("preReleaseVersion") is { } pre ? marketing.GetValueOrDefault(pre) : null,
                        b.Attr("version") ?? "?",
                        state switch { "VALID" => ReleaseStage.Live, "PROCESSING" => ReleaseStage.Processing, "FAILED" or "INVALID" => ReleaseStage.Rejected, "EXPIRED" => ReleaseStage.Retired, _ => ReleaseStage.Other },
                        state,
                        DateTime.TryParse(b.Attr("uploadedDate"), out var uploaded) ? uploaded.ToUniversalTime() : null,
                        []);
                })
                .ToList());
    }

    private async Task<StoreReleases> GoogleAsync(ReadOnlyMemory<byte> secret, string packageName, CancellationToken ct)
    {
        var session = await google.OpenAsync(secret, ct);
        var app = GooglePublisher.App(packageName);

        return await session.WithEditAsync(packageName, commit: false, async editId =>
        {
            var tracks = (await session.GetAsync($"{app}/edits/{editId}/tracks", ct))?["tracks"]?.AsArray().OfType<JsonNode>().ToList() ?? [];
            var bundles = (await session.GetAsync($"{app}/edits/{editId}/bundles", ct))?["bundles"]?.AsArray().OfType<JsonNode>().ToList() ?? [];

            var versions = new List<VersionInfo>();
            var tracksByCode = new Dictionary<string, List<string>>();

            foreach (var track in tracks)
            {
                var trackName = track["track"]?.GetValue<string>() ?? "?";
                foreach (var release in track["releases"]?.AsArray().OfType<JsonNode>() ?? [])
                {
                    var codes = release["versionCodes"]?.AsArray().Select(c => c!.ToString()).ToList() ?? [];
                    foreach (var code in codes)
                    {
                        if (!tracksByCode.TryGetValue(code, out var list)) tracksByCode[code] = list = [];
                        if (!list.Contains(trackName)) list.Add(trackName);
                    }

                    var status = release["status"]?.GetValue<string>() ?? "";
                    var fraction = release["userFraction"] is JsonValue f && f.TryGetValue<double>(out var value) ? value * 100 : (double?)null;
                    var notes = release["releaseNotes"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>();

                    versions.Add(new VersionInfo(
                        release["name"]?.GetValue<string>() ?? (codes.Count > 0 ? string.Join(", ", codes) : "?"),
                        GoogleStage(trackName, status), status, trackName, null, fraction, codes, notes));
                }
            }

            var builds = bundles
                .Select(b => b["versionCode"]!.ToString())
                .OrderByDescending(c => long.TryParse(c, out var n) ? n : 0)
                .Select(code => new BuildInfo(null, code,
                    tracksByCode.ContainsKey(code) ? ReleaseStage.Live : ReleaseStage.Other,
                    tracksByCode.ContainsKey(code) ? "IN_TRACK" : "UPLOADED",
                    null, tracksByCode.GetValueOrDefault(code) ?? []))
                .ToList();

            return new StoreReleases(Store.GooglePlay, packageName, versions, builds);
        }, ct);
    }

    public static ReleaseStage AppleStage(string state) => state switch
    {
        "READY_FOR_SALE" or "READY_FOR_DISTRIBUTION" or "PREORDER_READY_FOR_SALE" => ReleaseStage.Live,
        "WAITING_FOR_REVIEW" or "IN_REVIEW" or "PENDING_DEVELOPER_RELEASE" or "PENDING_APPLE_RELEASE" or "PROCESSING_FOR_APP_STORE"
            or "PROCESSING_FOR_DISTRIBUTION" or "ACCEPTED" or "READY_FOR_REVIEW" or "WAITING_FOR_EXPORT_COMPLIANCE" => ReleaseStage.InReview,
        "PREPARE_FOR_SUBMISSION" or "DEVELOPER_REJECTED" => ReleaseStage.Draft,
        "REJECTED" or "METADATA_REJECTED" or "INVALID_BINARY" => ReleaseStage.Rejected,
        "REPLACED_WITH_NEW_VERSION" or "REMOVED_FROM_SALE" or "DEVELOPER_REMOVED_FROM_SALE" => ReleaseStage.Retired,
        _ => ReleaseStage.Other
    };

    public static ReleaseStage GoogleStage(string track, string status) => status switch
    {
        "completed" => track == "production" ? ReleaseStage.Live : ReleaseStage.Rolling,
        "inProgress" => ReleaseStage.Rolling,
        "draft" => ReleaseStage.Draft,
        "halted" => ReleaseStage.Retired,
        _ => ReleaseStage.Other
    };
}
