using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Account;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Api.Features.Credentials;
using Flarelytics.Api.Features.Icons;
using Flarelytics.Api.Features.Manage;
using Flarelytics.Api.Features.Metrics;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Api.Features.Projects;
using Flarelytics.Api.Features.Setup;
using Flarelytics.Api.Features.Social;
using Flarelytics.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureApi();
builder.ConfigureDatabase();
builder.ConfigureAuthentication();
builder.ConfigureRateLimiting();
builder.ConfigureEmail();
builder.ConfigureSecrets();

// La sincronizzazione con gli store gira in questo stesso processo: in
// un'installazione self-hosted c'è un'istanza sola, e un container solo è più
// semplice da installare e da aggiornare. Si spegne nei test.
if (builder.Configuration.GetValue("Worker:Enabled", true))
{
    builder.Services.AddFlarelyticsSync();
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

await app.MigrateDatabaseAsync();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
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

// Il pannello, se l'immagine lo contiene (wwwroot): ogni percorso che non è
// un file e non è l'API riceve index.html, e la rotta la gestisce React.
app.MapFrontend();

app.Run();

/// <summary>Pubblica solo perché <c>WebApplicationFactory&lt;Program&gt;</c> dei test ci si possa agganciare.</summary>
public partial class Program;
