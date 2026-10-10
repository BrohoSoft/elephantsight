using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Account;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Api.Features.Backups;
using Flarelytics.Api.Features.Credentials;
using Flarelytics.Api.Features.Icons;
using Flarelytics.Api.Features.Instance;
using Flarelytics.Api.Features.Logs;
using Flarelytics.Api.Features.Manage;
using Flarelytics.Api.Features.Metrics;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Api.Features.Overview;
using Flarelytics.Api.Features.Projects;
using Flarelytics.Api.Features.PublicApi;
using Flarelytics.Api.Features.Setup;
using Flarelytics.Api.Features.Social;
using Flarelytics.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

// `restore <file>`: il ripristino di un backup, a istanza ferma (vedi BackupCommand).
// Gli argomenti del comando non vanno nella configurazione.
var restore = args.FirstOrDefault() == Flarelytics.Api.Features.Backups.BackupCommand.Name ? args[1..] : null;
var builder = WebApplication.CreateBuilder(restore is null ? args : []);

// I log anche a database, per leggerli dal pannello (pagina Log).
Flarelytics.Core.Logging.StoredLogsExtensions.AddStoredLogs(builder.Logging);

builder.ConfigureApi();
builder.ConfigureDatabase();
builder.ConfigureAuthentication();
builder.ConfigureRateLimiting();
builder.ConfigureEmail();
builder.ConfigureSecrets();
builder.Services.AddFlarelyticsBackups();

// Le impostazioni dell'istanza inserite dal pannello (SMTP, app social):
// aggiunte per ultime, vincono sul .env, e cambiano senza riavviare.
var instanceSettings = new Flarelytics.Core.Instance.InstanceSettingsConfigurationSource();
((IConfigurationBuilder)builder.Configuration).Add(instanceSettings);
builder.Services.AddSingleton(instanceSettings.Provider);
builder.Services.AddScoped<Flarelytics.Core.Instance.InstanceSettingsStore>();

// La sincronizzazione con gli store gira in questo stesso processo: in
// un'installazione self-hosted c'è un'istanza sola, e un container solo è più
// semplice da installare e da aggiornare. Si spegne nei test.
if (builder.Configuration.GetValue("Worker:Enabled", true))
{
    builder.Services.AddFlarelyticsSync();
    // I backup programmati: solo se la funzione c'è (BACKUPS_ENABLED), e qui
    // dentro perché il worker gira in un'istanza sola, come la sincronizzazione.
    if (builder.Configuration.GetValue("Backups:Enabled", true))
    {
        builder.Services.AddHostedService<Flarelytics.Core.Backups.BackupWorker>();
    }
}

// ASP.NET usa le sue chiavi interne (DataProtection) per alcuni cookie
// tecnici: accanto alle nostre, così sopravvivono all'aggiornamento del
// container invece di rigenerarsi a ogni avvio.
if (builder.Configuration["Secrets:KeysDirectory"] is { Length: > 0 } keysDirectory && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(keysDirectory, "dataprotection")));
}

// Dietro un proxy (Caddy, Traefik, nginx…): senza, l'IP di ogni richiesta
// sarebbe quello del proxy, e il tetto per IP sulle rotte di accesso
// diventerebbe un tetto unico per tutti.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Prima delle migration: il ripristino rimette il database com'era nel backup.
if (restore is not null)
{
    return await Flarelytics.Api.Features.Backups.BackupCommand.RunAsync(restore, app.Services, app.Configuration);
}

await app.MigrateDatabaseAsync();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<Flarelytics.Core.Instance.InstanceSettingsStore>().LoadAsync(CancellationToken.None);
}

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseSiteVerificationFiles();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/health", () => Results.Ok(new { status = "online" })).AllowAnonymous();

var api = app.MapGroup("/api/v1");
api.MapSetup();
api.MapAuth();
api.MapOrgs();
api.MapAccount();
api.MapMembers();
api.MapMetrics();
api.MapIcons();
api.MapProjects();
api.MapCredentials();
api.MapReleases();
api.MapReviews();
api.MapListing();
api.MapSecretFiles();
api.MapBuilds();
api.MapSocialAccounts();
api.MapSocialPosts();
api.MapSocialRecurring();
api.MapLogs();
api.MapInstance();
api.MapOverview();
api.MapApiKeys();
api.MapPublicApi();
// Con BACKUPS_ENABLED=false le rotte non esistono: rispondono 404 come qualsiasi rotta sconosciuta.
if (app.Configuration.GetValue("Backups:Enabled", true)) api.MapBackups();

// Il pannello, se l'immagine lo contiene (wwwroot): ogni percorso che non è
// un file e non è l'API riceve index.html, e la rotta la gestisce React.
app.MapFrontend();

app.Run();
return 0;

/// <summary>Pubblica solo perché <c>WebApplicationFactory&lt;Program&gt;</c> dei test ci si possa agganciare.</summary>
public partial class Program;
