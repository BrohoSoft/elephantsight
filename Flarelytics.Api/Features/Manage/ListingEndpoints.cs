using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Management;
using Flarelytics.Core.Stores;
using FluentValidation;

namespace Flarelytics.Api.Features.Manage;

/// <summary>
/// La pagina dello store di un progetto: testi per lingua e screenshot, su
/// App Store e Google Play. Rotte sotto <c>/orgs/{orgId}/projects/{projectId}/listing</c>.
/// </summary>
/// <remarks>
/// I file arrivano come multipart: sono immagini binarie. Niente antiforgery:
/// l'autenticazione è il bearer token, che un sito terzo non può mandare.
/// </remarks>
public static class ListingEndpoints
{
    private const long MaxImageBytes = 15 * 1024 * 1024;

    public static void MapListing(this IEndpointRouteBuilder api)
    {
        var listing = api.MapOrgGroup("/projects/{projectId:guid}/listing");
        listing.MapGet("", Get);
        listing.MapGet("/screenshots", Screenshots);

        var write = listing.MapGroup("").RequireOrgRole(OrgRole.Admin);
        write.MapPut("/app-store/{locale}", UpdateApple).Validating<AppleTextRequest>();
        write.MapPut("/google-play/{language}", UpdateGoogle).Validating<GoogleTextRequest>();
        write.MapPost("/screenshots", Upload).DisableAntiforgery();
        write.MapDelete("/screenshots", Delete);
    }

    /// <summary>I testi di entrambi gli store; uno store che non risponde non blocca l'altro.</summary>
    private static async Task<IResult> Get(Guid projectId, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var apps = await ProjectAppsLoader.LoadAsync(db, projectId, ct);
        var apple = apps.SingleOrDefault(a => a.Store == Store.AppStore);
        var google = apps.SingleOrDefault(a => a.Store == Store.GooglePlay);

        return Results.Ok(new ListingResponse(
            apple is null ? null : await Try(() => listings.GetAppleAsync(apple.Credential, apple.ExternalAppId, ct)),
            google is null ? null : await Try(() => listings.GetGoogleAsync(google.Credential, google.ExternalAppId, ct))));
    }

    private static async Task<IResult> UpdateApple(Guid projectId, string locale, AppleTextRequest req, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var app = await ProjectAppsLoader.LoadAsync(db, projectId, Store.AppStore, ct);
        await listings.UpdateAppleAsync(app.Credential, app.ExternalAppId, new AppleLocaleText(locale, null,
            Clean(req.Name), Clean(req.Subtitle), Clean(req.PrivacyPolicyUrl), null,
            Clean(req.Description), Clean(req.Keywords), Clean(req.WhatsNew), Clean(req.PromotionalText), Clean(req.MarketingUrl), Clean(req.SupportUrl)), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateGoogle(Guid projectId, string language, GoogleTextRequest req, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var app = await ProjectAppsLoader.LoadAsync(db, projectId, Store.GooglePlay, ct);
        await listings.UpdateGoogleAsync(app.Credential, app.ExternalAppId,
            new GoogleLocaleText(language, req.Title.Trim(), req.ShortDescription.Trim(), req.FullDescription.Trim(), Clean(req.Video)), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Screenshots(Guid projectId, Store store, string locale, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var app = await ProjectAppsLoader.LoadAsync(db, projectId, store, ct);
        return Results.Ok(store == Store.AppStore
            ? await listings.GetAppleScreenshotsAsync(app.Credential, app.ExternalAppId, locale, ct)
            : await listings.GetGoogleImagesAsync(app.Credential, app.ExternalAppId, locale, ct));
    }

    /// <summary>Carica un'immagine: <c>store</c>, <c>locale</c>, <c>group</c> (tipo di schermo o d'immagine) e il file.</summary>
    private static async Task<IResult> Upload(Guid projectId, HttpRequest request, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? throw ApiProblem.BadRequest("file_missing", "Manca il file.");
        if (!Enum.TryParse<Store>(form["store"], out var store)) throw ApiProblem.BadRequest("store_missing", "Indica lo store.");
        var locale = form["locale"].ToString();
        var group = form["group"].ToString();

        if (file.Length is 0 or > MaxImageBytes) throw ApiProblem.BadRequest("file_size", "L'immagine deve pesare meno di 15 MB.");
        if (file.ContentType is not ("image/png" or "image/jpeg")) throw ApiProblem.BadRequest("file_type", "Sono accettate immagini PNG o JPEG.");
        if (store == Store.GooglePlay && !ListingService.GoogleImageTypes.Contains(group)) throw ApiProblem.BadRequest("group", "Tipo d'immagine sconosciuto.");
        if (string.IsNullOrWhiteSpace(locale) || string.IsNullOrWhiteSpace(group)) throw ApiProblem.BadRequest("group", "Indica lingua e tipo di schermo.");

        var app = await ProjectAppsLoader.LoadAsync(db, projectId, store, ct);

        // In memoria e non in streaming: Apple vuole il file a pezzi e poi
        // l'MD5 intero, quindi serve poterlo rileggere. Sono al massimo 15 MB.
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Seek(0, SeekOrigin.Begin);

        if (store == Store.AppStore) await listings.UploadAppleScreenshotAsync(app.Credential, app.ExternalAppId, locale, group, Path.GetFileName(file.FileName), buffer, ct);
        else await listings.UploadGoogleImageAsync(app.Credential, app.ExternalAppId, locale, group, file.ContentType, buffer, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Delete(Guid projectId, Store store, string locale, string group, string id, FlarelyticsDbContext db, ListingService listings, CancellationToken ct)
    {
        var app = await ProjectAppsLoader.LoadAsync(db, projectId, store, ct);
        if (store == Store.AppStore) await listings.DeleteAppleScreenshotAsync(app.Credential, id, ct);
        else await listings.DeleteGoogleImageAsync(app.Credential, app.ExternalAppId, locale, group, id, ct);
        return Results.NoContent();
    }

    private static async Task<StoreResult<T>> Try<T>(Func<Task<T>> load)
    {
        try { return new StoreResult<T>(await load(), null); }
        catch (StoreAccessException e) { return new StoreResult<T>(default, e.Message); }
        catch (HttpRequestException) { return new StoreResult<T>(default, "Lo store non risponde: riprova fra poco."); }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Il risultato di uno store, o il motivo per cui non c'è.</summary>
public record StoreResult<T>(T? Data, string? Error);

public record ListingResponse(StoreResult<AppleListing>? AppStore, StoreResult<GoogleListing>? GooglePlay);

public record AppleTextRequest(
    string? Name, string? Subtitle, string? PrivacyPolicyUrl, string? Description, string? Keywords,
    string? WhatsNew, string? PromotionalText, string? MarketingUrl, string? SupportUrl);

public record GoogleTextRequest(string Title, string ShortDescription, string FullDescription, string? Video);

/// <summary>I limiti di Apple: meglio fermarsi qui con un messaggio chiaro che farsi rispondere un 409 generico.</summary>
public class AppleTextRequestValidator : AbstractValidator<AppleTextRequest>
{
    public AppleTextRequestValidator()
    {
        RuleFor(x => x.Name).MaximumLength(30).WithMessage("Il nome ha al massimo 30 caratteri.");
        RuleFor(x => x.Subtitle).MaximumLength(30).WithMessage("Il sottotitolo ha al massimo 30 caratteri.");
        RuleFor(x => x.Keywords).MaximumLength(100).WithMessage("Le parole chiave hanno al massimo 100 caratteri in tutto.");
        RuleFor(x => x.Description).MaximumLength(4000).WithMessage("La descrizione ha al massimo 4000 caratteri.");
        RuleFor(x => x.WhatsNew).MaximumLength(4000).WithMessage("Le novità hanno al massimo 4000 caratteri.");
        RuleFor(x => x.PromotionalText).MaximumLength(170).WithMessage("Il testo promozionale ha al massimo 170 caratteri.");
        RuleFor(x => x.PrivacyPolicyUrl).MaximumLength(2000);
        RuleFor(x => x.MarketingUrl).MaximumLength(2000);
        RuleFor(x => x.SupportUrl).MaximumLength(2000);
    }
}

public class GoogleTextRequestValidator : AbstractValidator<GoogleTextRequest>
{
    public GoogleTextRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(30).WithMessage("Il titolo ha al massimo 30 caratteri.");
        RuleFor(x => x.ShortDescription).NotEmpty().MaximumLength(80).WithMessage("La descrizione breve ha al massimo 80 caratteri.");
        RuleFor(x => x.FullDescription).NotEmpty().MaximumLength(4000).WithMessage("La descrizione completa ha al massimo 4000 caratteri.");
        RuleFor(x => x.Video).MaximumLength(500);
    }
}
