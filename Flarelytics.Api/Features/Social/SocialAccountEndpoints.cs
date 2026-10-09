using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Social;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Features.Social;

/// <summary>
/// Gli account social dell'organizzazione. Rotte sotto <c>/orgs/{orgId}/social/accounts</c>
/// e, per il login Meta, <c>/orgs/{orgId}/social/meta</c>.
/// </summary>
/// <remarks>
/// <para>Come per le chiavi degli store, il segreto entra e non esce: si
/// verifica con la rete, si cifra, e nessuna rotta lo restituisce.</para>
///
/// <para><b>Login Meta.</b> Facebook rimanda il browser a una pagina del
/// pannello (<c>/social/meta/callback</c>), non all'API: così il codice arriva
/// all'API con la sessione dell'utente e passa dallo stesso controllo di
/// organizzazione e ruolo di tutto il resto, senza una rotta anonima che
/// scrive nel tenant. Lo <c>state</c> è firmato e scade, e lega il login
/// all'utente e all'organizzazione che l'hanno iniziato.</para>
/// </remarks>
public static class SocialAccountEndpoints
{
    public const string MetaCallbackPath = "/social/meta/callback";
    public const string InstagramCallbackPath = "/social/instagram/callback";
    public const string TikTokCallbackPath = "/social/tiktok/callback";

    public static void MapSocialAccounts(this IEndpointRouteBuilder api)
    {
        var social = api.MapOrgGroup("/social");
        social.MapGet("/accounts", List);

        var admin = social.MapGroup("").RequireOrgRole(OrgRole.Admin);
        admin.MapPost("/accounts/bluesky", ConnectBluesky).Validating<ConnectBlueskyRequest>();
        admin.MapPost("/accounts/mastodon", ConnectMastodon).Validating<ConnectMastodonRequest>();
        admin.MapDelete("/accounts/{accountId:guid}", Delete);

        admin.MapPost("/meta/start", StartMeta);
        admin.MapPost("/meta/complete", CompleteMeta).Validating<CompleteMetaRequest>();
        admin.MapPost("/meta/accounts", AddMetaAccounts).Validating<AddMetaAccountsRequest>();

        admin.MapPost("/instagram/start", StartInstagram);
        admin.MapPost("/instagram/complete", CompleteInstagram).Validating<CompleteMetaRequest>();

        admin.MapPost("/tiktok/start", StartTikTok);
        admin.MapPost("/tiktok/complete", CompleteTikTok).Validating<CompleteMetaRequest>();
        admin.MapGet("/accounts/{accountId:guid}/tiktok-creator", TikTokCreator);
    }

    private static async Task<IResult> List(FlarelyticsDbContext db, CancellationToken ct)
    {
        var accounts = await db.Set<SocialAccount>().AsNoTracking().OrderBy(a => a.Network).ThenBy(a => a.Name).ToListAsync(ct);
        return Results.Ok(accounts.Select(SocialAccountResponse.From));
    }

    private static async Task<IResult> ConnectBluesky(ConnectBlueskyRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        BlueskyClient bluesky, FieldProtector protector, CancellationToken ct)
    {
        var service = NormalizeUrl(req.ServiceUrl) ?? BlueskyClient.DefaultService;
        var handle = req.Handle.Trim().TrimStart('@');
        var session = await bluesky.LoginAsync(service, handle, req.AppPassword.Trim(), ct);

        var account = await UpsertAsync(db, org.TenantId, SocialNetwork.Bluesky, session.Did, service, principal.UserId(), ct);
        account.Reconnect(Protect(protector, account, req.AppPassword.Trim()), session.Handle, session.Handle, null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialAccountResponse.From(account));
    }

    private static async Task<IResult> ConnectMastodon(ConnectMastodonRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        MastodonClient mastodon, FieldProtector protector, CancellationToken ct)
    {
        var instance = NormalizeUrl(req.InstanceUrl) ?? throw ApiProblem.BadRequest("instance", "Indica l'indirizzo dell'istanza, per esempio mastodon.social.");
        var token = req.AccessToken.Trim();
        var me = await mastodon.VerifyAsync(instance, token, ct);
        var maxCharacters = await mastodon.MaxCharactersAsync(instance, ct);
        var host = new Uri(instance).Host;

        var account = await UpsertAsync(db, org.TenantId, SocialNetwork.Mastodon, $"{me.Id}@{host}", instance, principal.UserId(), ct);
        account.Reconnect(Protect(protector, account, token), me.Name, $"@{me.Username}@{host}", maxCharacters);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialAccountResponse.From(account));
    }

    /// <summary>
    /// Scollega l'account. I post già pubblicati restano nello storico; quelli
    /// ancora da pubblicare su questo account non partiranno più.
    /// </summary>
    private static async Task<IResult> Delete(Guid accountId, FlarelyticsDbContext db, CancellationToken ct)
    {
        var account = await db.Set<SocialAccount>().SingleOrDefaultAsync(a => a.Id == accountId, ct) ?? throw ApiProblem.NotFound("Account");
        var pending = await db.Set<SocialPostTarget>()
            .Where(t => t.AccountId == accountId && (t.Status == SocialTargetStatus.Pending || t.Status == SocialTargetStatus.Failed)).ToListAsync(ct);

        db.RemoveRange(pending);
        db.Remove(account);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // --- Meta (Instagram e Pagine Facebook) ---

    private static IResult StartMeta(ClaimsPrincipal principal, CurrentOrg org, MetaGraphClient meta, IOptions<SocialOptions> options,
        IDataProtectionProvider protection)
    {
        if (!options.Value.Meta.Enabled)
            throw ApiProblem.BadRequest("meta_not_configured", "Per Instagram e Facebook serve un'app Meta: imposta META_APP_ID e META_APP_SECRET e riavvia.");

        var state = StateProtector(protection).Protect($"{org.TenantId:N}|{principal.UserId():N}", TimeSpan.FromMinutes(15));
        return Results.Ok(new { url = meta.AuthorizeUrl(MetaRedirectUri(options), state) });
    }

    /// <summary>
    /// Il ritorno dal login di Facebook: scambia il codice e restituisce gli
    /// account trovati, senza collegarne ancora nessuno. La scelta torna con
    /// <c>selection</c>, che contiene i token cifrati e scade in mezz'ora: il
    /// browser lo porta avanti ma non lo può leggere.
    /// </summary>
    private static async Task<IResult> CompleteMeta(CompleteMetaRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        MetaGraphClient meta, IOptions<SocialOptions> options, IDataProtectionProvider protection, CancellationToken ct)
    {
        CheckState(protection, req.State, org, principal, "Facebook");
        var userToken = await meta.ExchangeCodeAsync(req.Code, MetaRedirectUri(options), ct);
        var pages = await meta.ListPagesAsync(userToken, ct);

        var candidates = new List<MetaCandidate>();
        foreach (var p in pages)
        {
            candidates.Add(new MetaCandidate($"fb:{p.Id}", SocialNetwork.FacebookPage, p.Id, p.Name, null, p.Token));
            if (p.InstagramId is { } igId)
                candidates.Add(new MetaCandidate($"ig:{igId}", SocialNetwork.Instagram, igId, p.InstagramUsername ?? p.Name, p.InstagramUsername is { } u ? "@" + u : null, p.Token));
        }

        var connected = await db.Set<SocialAccount>().Where(a => a.Network == SocialNetwork.Instagram || a.Network == SocialNetwork.FacebookPage)
            .Select(a => a.ExternalId).ToListAsync(ct);
        var selection = SelectionProtector(protection).Protect(JsonSerializer.Serialize(candidates), TimeSpan.FromMinutes(30));

        return Results.Ok(new MetaCandidatesResponse(
            candidates.Select(c => new MetaCandidateResponse(c.Key, c.Network, c.Name, c.Handle, connected.Contains(c.ExternalId))).ToList(),
            selection));
    }

    private static async Task<IResult> AddMetaAccounts(AddMetaAccountsRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        FieldProtector protector, IDataProtectionProvider protection, CancellationToken ct)
    {
        List<MetaCandidate> candidates;
        try
        {
            candidates = JsonSerializer.Deserialize<List<MetaCandidate>>(SelectionProtector(protection).Unprotect(req.Selection))!;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw ApiProblem.BadRequest("meta_selection", "La scelta è scaduta: ricollega Facebook.");
        }

        var added = new List<SocialAccount>();
        foreach (var c in candidates.Where(c => req.Keys.Contains(c.Key)))
        {
            var account = await UpsertAsync(db, org.TenantId, c.Network, c.ExternalId, null, principal.UserId(), ct);
            account.Reconnect(Protect(protector, account, c.Token), c.Name, c.Handle, null);
            added.Add(account);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(added.Select(SocialAccountResponse.From));
    }

    // --- Instagram Login (account senza Pagina Facebook) ---

    private static IResult StartInstagram(ClaimsPrincipal principal, CurrentOrg org, InstagramLoginClient instagram, IOptions<SocialOptions> options,
        IDataProtectionProvider protection)
    {
        if (!options.Value.Instagram.Enabled)
            throw ApiProblem.BadRequest("instagram_not_configured", "Per collegare Instagram senza Facebook imposta INSTAGRAM_APP_ID e INSTAGRAM_APP_SECRET e riavvia.");

        var state = StateProtector(protection).Protect($"{org.TenantId:N}|{principal.UserId():N}", TimeSpan.FromMinutes(15));
        return Results.Ok(new { url = instagram.AuthorizeUrl(InstagramRedirectUri(options), state) });
    }

    /// <summary>
    /// Il ritorno dal login di Instagram. Un login è un account solo, quindi
    /// non c'è niente da scegliere: si collega subito (o si ricollega, se c'era).
    /// </summary>
    private static async Task<IResult> CompleteInstagram(CompleteMetaRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        InstagramLoginClient instagram, FieldProtector protector, IOptions<SocialOptions> options, IDataProtectionProvider protection, CancellationToken ct)
    {
        CheckState(protection, req.State, org, principal, "Instagram");

        var token = await instagram.ExchangeCodeAsync(req.Code, InstagramRedirectUri(options), ct);
        var profile = await instagram.ProfileAsync(token.AccessToken, ct);
        if (profile.AccountType is "PERSONAL")
            throw ApiProblem.BadRequest("instagram_personal", "È un account Instagram personale: per pubblicare serve un account professionale (Business o Creator), si cambia dalle impostazioni di Instagram.");

        var account = await UpsertAsync(db, org.TenantId, SocialNetwork.Instagram, profile.UserId, InstagramLoginClient.Host, principal.UserId(), ct);
        account.Reconnect(Protect(protector, account, token.AccessToken), string.IsNullOrWhiteSpace(profile.Name) ? profile.Username : profile.Name,
            "@" + profile.Username, null, token.ExpiresAtUtc, InstagramLoginClient.Host);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialAccountResponse.From(account));
    }

    // --- TikTok ---

    private static IResult StartTikTok(ClaimsPrincipal principal, CurrentOrg org, TikTokClient tiktok, IOptions<SocialOptions> options,
        IDataProtectionProvider protection)
    {
        if (!options.Value.TikTok.Enabled)
            throw ApiProblem.BadRequest("tiktok_not_configured", "Per TikTok imposta TIKTOK_CLIENT_KEY e TIKTOK_CLIENT_SECRET e riavvia.");

        var state = StateProtector(protection).Protect($"{org.TenantId:N}|{principal.UserId():N}", TimeSpan.FromMinutes(15));
        return Results.Ok(new { url = tiktok.AuthorizeUrl(TikTokRedirectUri(options), state) });
    }

    /// <summary>
    /// Il ritorno dal login di TikTok: due token (24 ore e un anno) cifrati
    /// insieme, e nome e handle dalle informazioni del creator.
    /// </summary>
    private static async Task<IResult> CompleteTikTok(CompleteMetaRequest req, ClaimsPrincipal principal, CurrentOrg org, FlarelyticsDbContext db,
        TikTokClient tiktok, FieldProtector protector, IOptions<SocialOptions> options, IDataProtectionProvider protection, CancellationToken ct)
    {
        CheckState(protection, req.State, org, principal, "TikTok");

        var tokens = await tiktok.ExchangeCodeAsync(req.Code, TikTokRedirectUri(options), ct);
        var creator = await tiktok.CreatorInfoAsync(tokens.AccessToken, ct);

        var account = await UpsertAsync(db, org.TenantId, SocialNetwork.TikTok, tokens.OpenId, null, principal.UserId(), ct);
        account.Reconnect(Protect(protector, account, TikTokSecret.From(tokens).ToJson()),
            string.IsNullOrWhiteSpace(creator.Nickname) ? creator.Username : creator.Nickname,
            string.IsNullOrWhiteSpace(creator.Username) ? null : "@" + creator.Username, null, tokens.ExpiresAtUtc);
        await db.SaveChangesAsync(ct);
        return Results.Ok(SocialAccountResponse.From(account));
    }

    /// <summary>
    /// Le informazioni del creator, fresche: TikTok vuole che il pannello le
    /// mostri mentre si prepara un post (chi pubblica, quali visibilità sono
    /// permesse, cosa ha disattivato, la durata massima).
    /// </summary>
    private static async Task<IResult> TikTokCreator(Guid accountId, FlarelyticsDbContext db, TikTokClient tiktok, FieldProtector protector, CancellationToken ct)
    {
        var account = await db.Set<SocialAccount>().SingleOrDefaultAsync(a => a.Id == accountId && a.Network == SocialNetwork.TikTok, ct)
            ?? throw ApiProblem.NotFound("Account");

        var bytes = protector.Unprotect(account.ProtectedSecret, account.SecretContext);
        var secret = TikTokSecret.Parse(Encoding.UTF8.GetString(bytes));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);

        try
        {
            var token = account.TokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(5)
                ? secret.AccessToken
                : await SocialPublisher.RenewTikTokAsync(account, secret, protector, tiktok, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await tiktok.CreatorInfoAsync(token, ct));
        }
        catch (SocialApiException e) when (e.Unauthorized)
        {
            account.MarkBroken(e.Message);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <summary>Lo <c>state</c> del login: firmato, a scadenza, e partito da questo utente in questa organizzazione.</summary>
    private static void CheckState(IDataProtectionProvider protection, string state, CurrentOrg org, ClaimsPrincipal principal, string network)
    {
        string payload;
        try
        {
            payload = StateProtector(protection).Unprotect(state);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw ApiProblem.BadRequest("oauth_state", $"Il collegamento con {network} è scaduto o non è partito da qui: riprova.");
        }
        if (payload != $"{org.TenantId:N}|{principal.UserId():N}")
            throw ApiProblem.BadRequest("oauth_state", $"Il collegamento con {network} è partito da un altro utente o da un'altra organizzazione.");
    }

    /// <summary>Il JSON della scelta, con il token della Pagina. Non lascia mai il server in chiaro.</summary>
    private record MetaCandidate(string Key, SocialNetwork Network, string ExternalId, string Name, string? Handle, string Token);

    private static ITimeLimitedDataProtector StateProtector(IDataProtectionProvider p) => p.CreateProtector("social-meta-state").ToTimeLimitedDataProtector();
    private static ITimeLimitedDataProtector SelectionProtector(IDataProtectionProvider p) => p.CreateProtector("social-meta-selection").ToTimeLimitedDataProtector();

    // Gli indirizzi di ritorno devono essere identici nella richiesta di login e
    // nello scambio del codice, e registrati nell'app. Stanno sul pannello
    // (AppUrl), dove il browser ha la sessione, non sull'indirizzo delle immagini.
    public static string MetaRedirectUri(IOptions<SocialOptions> options) => options.Value.AppUrl + MetaCallbackPath;
    public static string InstagramRedirectUri(IOptions<SocialOptions> options) => options.Value.AppUrl + InstagramCallbackPath;
    public static string TikTokRedirectUri(IOptions<SocialOptions> options) => options.Value.AppUrl + TikTokCallbackPath;

    // --- in comune ---

    /// <summary>L'account se è già collegato (allora è un ricollegamento), altrimenti uno nuovo.</summary>
    private static async Task<SocialAccount> UpsertAsync(FlarelyticsDbContext db, Guid tenantId, SocialNetwork network, string externalId, string? serverUrl,
        Guid userId, CancellationToken ct)
    {
        var existing = await db.Set<SocialAccount>().SingleOrDefaultAsync(a => a.Network == network && a.ExternalId == externalId, ct);
        if (existing is not null) return existing;

        var account = SocialAccount.Create(tenantId, network, externalId, externalId, null, serverUrl, userId);
        db.Add(account);
        return account;
    }

    private static string Protect(FieldProtector protector, SocialAccount account, string secret) =>
        protector.Protect(Encoding.UTF8.GetBytes(secret), account.SecretContext);

    /// <summary>"mastodon.social" → "https://mastodon.social". Solo https: il token viaggia in ogni richiesta.</summary>
    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().TrimEnd('/');
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw ApiProblem.BadRequest("server_url", "L'indirizzo del server deve essere https.");
        return $"https://{uri.Authority}";
    }
}

public record SocialAccountResponse(Guid Id, SocialNetwork Network, string Name, string? Handle, string? ServerUrl,
    SocialAccountStatus Status, string? StatusMessage, NetworkLimits Limits, DateTime CreatedAtUtc)
{
    public static SocialAccountResponse From(SocialAccount a) =>
        new(a.Id, a.Network, a.Name, a.Handle, a.ServerUrl, a.Status, a.StatusMessage, SocialRules.For(a), a.CreatedAtUtc);
}

public record MetaCandidateResponse(string Key, SocialNetwork Network, string Name, string? Handle, bool AlreadyConnected);

/// <param name="Selection">Da rimandare con le chiavi scelte: contiene i token, cifrati.</param>
public record MetaCandidatesResponse(IReadOnlyList<MetaCandidateResponse> Candidates, string Selection);

/// <param name="ServiceUrl">Il PDS, solo per chi non sta su bsky.social.</param>
public record ConnectBlueskyRequest(string Handle, string AppPassword, string? ServiceUrl);

public record ConnectMastodonRequest(string InstanceUrl, string AccessToken);

public record CompleteMetaRequest(string Code, string State);

public record AddMetaAccountsRequest(string Selection, IReadOnlyList<string> Keys);

public class ConnectBlueskyRequestValidator : AbstractValidator<ConnectBlueskyRequest>
{
    public ConnectBlueskyRequestValidator()
    {
        RuleFor(x => x.Handle).NotEmpty().MaximumLength(253);
        RuleFor(x => x.AppPassword).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ServiceUrl).MaximumLength(200);
    }
}

public class ConnectMastodonRequestValidator : AbstractValidator<ConnectMastodonRequest>
{
    public ConnectMastodonRequestValidator()
    {
        RuleFor(x => x.InstanceUrl).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AccessToken).NotEmpty().MaximumLength(500);
    }
}

public class CompleteMetaRequestValidator : AbstractValidator<CompleteMetaRequest>
{
    public CompleteMetaRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.State).NotEmpty().MaximumLength(2000);
    }
}

public class AddMetaAccountsRequestValidator : AbstractValidator<AddMetaAccountsRequest>
{
    public AddMetaAccountsRequestValidator()
    {
        RuleFor(x => x.Selection).NotEmpty().MaximumLength(200_000);
        RuleFor(x => x.Keys).NotEmpty().Must(k => k.Count <= 200);
    }
}
