using System.Security.Claims;
using Flarelytics.Api.Common;
using Flarelytics.Api.Email;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Instance;
using Flarelytics.Core.Social.Media;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Features.Instance;

/// <summary>
/// L'installazione: SMTP e app social (al posto del .env, senza riavviare) e i
/// suoi amministratori. Rotte sotto <c>/instance/settings</c> e
/// <c>/instance/admins</c>, solo per gli amministratori dell'istanza.
/// </summary>
/// <remarks>
/// I segreti (password SMTP, app secret) entrano e non escono: la risposta
/// dice solo se ci sono. Gli altri campi si vedono, con da dove arrivano
/// (pannello o .env): quello del pannello vince.
/// </remarks>
public static class InstanceEndpoints
{
    public static void MapInstance(this IEndpointRouteBuilder api)
    {
        var instance = api.MapGroup("/instance").RequireAuthorization().AddEndpointFilter<InstanceAdminFilter>();
        instance.MapGet("/settings", GetSettings);
        instance.MapPut("/settings/{group}", SaveSettings);
        instance.MapDelete("/settings/{group}", ClearSettings);
        instance.MapPost("/settings/smtp/test", TestSmtp);
        instance.MapPost("/settings/bunny/test", TestBunny);

        instance.MapGet("/admins", ListAdmins);
        instance.MapPost("/admins", AddAdmin).Validating<AddAdminRequest>();
        instance.MapDelete("/admins/{userId:guid}", RemoveAdmin);
    }

    private static async Task<IResult> GetSettings(InstanceSettingsStore store, IConfiguration configuration, CancellationToken ct)
    {
        var panel = await store.ReadAsync(ct);
        var groups = InstanceSettingCatalog.Fields.GroupBy(f => f.Group).Select(g => new SettingsGroup(g.Key, g.Select(f =>
        {
            var effective = configuration[f.ConfigPath];
            var set = !string.IsNullOrWhiteSpace(effective);
            var source = panel.ContainsKey(f.Key) ? "panel" : set ? "env" : null;
            return new SettingValue(f.Name, f.Secret, f.Secret ? null : effective, set, source);
        }).ToList())).ToList();
        return Results.Ok(groups);
    }

    /// <summary>I campi del gruppo: null lascia com'è (i segreti non si rimandano), vuoto toglie il valore del pannello.</summary>
    private static async Task<IResult> SaveSettings(string group, Dictionary<string, string?> values, ClaimsPrincipal principal, InstanceSettingsStore store,
        IConfiguration configuration, CancellationToken ct)
    {
        if (!InstanceSettingCatalog.IsGroup(group)) throw ApiProblem.NotFound("Gruppo di impostazioni");
        var unknown = values.Keys.Where(k => InstanceSettingCatalog.Group(group).All(f => f.Name != k)).ToList();
        if (unknown.Count > 0) throw ApiProblem.BadRequest("unknown_setting", $"Campi sconosciuti: {string.Join(", ", unknown)}.");
        if (values.Values.Any(v => v is { Length: > 2000 })) throw ApiProblem.BadRequest("setting_too_long", "Un valore è troppo lungo.");
        if (group == "smtp" && values.GetValueOrDefault("port") is { Length: > 0 } port && (!int.TryParse(port, out var p) || p is < 1 or > 65535))
            throw ApiProblem.BadRequest("smtp_port", "La porta SMTP è un numero fra 1 e 65535 (di solito 587, o 465).");
        if (group == "bunny" && values.GetValueOrDefault("region") is { Length: > 0 } region && !BunnyStorageOptions.Regions.ContainsKey(region.Trim()))
            throw ApiProblem.BadRequest("bunny_region",
                $"Regione sconosciuta: usa {string.Join(", ", BunnyStorageOptions.Regions.Keys.Where(k => k.Length > 0))}, o lascia vuoto per Falkenstein.");

        await store.SaveAsync(group, values, principal.UserId(), ct);
        return await GetSettings(store, configuration, ct);
    }

    private static async Task<IResult> ClearSettings(string group, InstanceSettingsStore store, IConfiguration configuration, CancellationToken ct)
    {
        if (!InstanceSettingCatalog.IsGroup(group)) throw ApiProblem.NotFound("Gruppo di impostazioni");
        await store.ClearAsync(group, ct);
        return await GetSettings(store, configuration, ct);
    }

    /// <summary>Un'email di prova all'amministratore, con l'SMTP attuale: l'errore del server torna così com'è.</summary>
    private static async Task<IResult> TestSmtp(ClaimsPrincipal principal, FlarelyticsDbContext db, IOptionsMonitor<SmtpOptions> smtp, CancellationToken ct)
    {
        var options = smtp.CurrentValue;
        if (!options.Enabled) throw ApiProblem.BadRequest("smtp_not_configured", "Manca il server SMTP o l'indirizzo del mittente.");

        var userId = principal.UserId();
        var email = await db.Set<User>().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await SmtpEmail.SendAsync(options, new EmailMessage(email, "Prova SMTP di ElephantSight",
                "Se leggi questa email, l'SMTP di ElephantSight funziona: inviti e recupero password partiranno da qui."), timeout.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ApiProblem.BadRequest("smtp_failed", $"Il server SMTP non ha accettato l'invio: {e.Message}");
        }
        return Results.Ok(new { sentTo = email });
    }

    /// <summary>
    /// Prova la storage zone con la configurazione attuale: scrive un file di
    /// prova, lo rilegge e lo cancella. L'errore dice cosa non va senza mai
    /// riportare la password.
    /// </summary>
    private static async Task<IResult> TestBunny(BunnyStorageClient bunny, IOptionsMonitor<MediaStorageOptions> media, CancellationToken ct)
    {
        if (!media.CurrentValue.Bunny.Configured)
            throw ApiProblem.BadRequest("bunny_not_configured", "Mancano la storage zone o la sua password (o la regione non è valida).");

        var probe = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        var path = $"_prova/{Guid.NewGuid():N}.bin";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await bunny.PutAsync(path, new MemoryStream(probe), probe.Length, timeout.Token);
            using (var response = await bunny.GetAsync(path, null, null, timeout.Token))
            {
                var back = response is null ? null : await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (back is null || !back.AsSpan().SequenceEqual(probe))
                    throw ApiProblem.BadRequest("bunny_failed", "Il file di prova è stato caricato ma non si rilegge uguale: controlla la storage zone.");
            }
            await bunny.DeleteAsync(path, timeout.Token);
        }
        catch (MediaStorageUnavailableException e)
        {
            throw ApiProblem.BadRequest("bunny_failed", e.Message);
        }
        return Results.Ok(new { host = media.CurrentValue.Bunny.Host });
    }

    private static async Task<IResult> ListAdmins(FlarelyticsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Set<User>().AsNoTracking().Where(u => u.IsInstanceAdmin).OrderBy(u => u.FullName)
            .Select(u => new InstanceAdmin(u.Id, u.Email, u.FullName)).ToListAsync(ct));

    /// <summary>Un utente che esiste già (entrato in almeno un'organizzazione) diventa amministratore dell'istanza.</summary>
    private static async Task<IResult> AddAdmin(AddAdminRequest req, FlarelyticsDbContext db, CancellationToken ct)
    {
        var email = User.NormalizeEmail(req.Email);
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Email == email, ct)
            ?? throw ApiProblem.NotFound("Un utente con questa email");
        user.SetInstanceAdmin(true);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new InstanceAdmin(user.Id, user.Email, user.FullName));
    }

    private static async Task<IResult> RemoveAdmin(Guid userId, FlarelyticsDbContext db, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Id == userId && u.IsInstanceAdmin, ct) ?? throw ApiProblem.NotFound("Amministratore");
        if (await db.Set<User>().CountAsync(u => u.IsInstanceAdmin, ct) <= 1)
            throw ApiProblem.Conflict("last_instance_admin", "È l'ultimo amministratore dell'istanza: nominane prima un altro.");
        user.SetInstanceAdmin(false);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>Le rotte dell'istanza: solo per chi la amministra (il flag si rilegge a ogni richiesta).</summary>
public class InstanceAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var userId = http.User.UserId();
        var db = http.RequestServices.GetRequiredService<FlarelyticsDbContext>();
        if (!await db.Set<User>().AnyAsync(u => u.Id == userId && u.IsInstanceAdmin, http.RequestAborted))
            throw ApiProblem.Forbidden("Solo un amministratore dell'istanza può vedere e cambiare queste impostazioni.");
        return await next(context);
    }
}

/// <param name="Value">Il valore in uso (null per i segreti, che non escono mai).</param>
/// <param name="Source"><c>panel</c> (impostato qui), <c>env</c> (dal .env) o null (non impostato).</param>
public record SettingValue(string Name, bool Secret, string? Value, bool Set, string? Source);

public record SettingsGroup(string Group, IReadOnlyList<SettingValue> Fields);

public record InstanceAdmin(Guid UserId, string Email, string FullName);

public record AddAdminRequest(string Email);

public class AddAdminRequestValidator : AbstractValidator<AddAdminRequest>
{
    public AddAdminRequestValidator() => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
}
