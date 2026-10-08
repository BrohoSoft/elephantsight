using Flarelytics.Api.Common;
using Flarelytics.Api.Features.Account;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Api.Features.Billing;
using Flarelytics.Api.Features.Credentials;
using Flarelytics.Api.Features.Metrics;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Api.Features.Projects;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureApi();
builder.ConfigureDatabase();
builder.ConfigureAuthentication();
builder.ConfigureRateLimiting();
builder.ConfigureEmail();
builder.ConfigureSecrets();
builder.ConfigureBilling();

// Dietro Caddy: senza, l'IP di ogni richiesta sarebbe quello del proxy, e il
// tetto per IP sulle rotte di accesso diventerebbe un tetto unico per tutti.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await app.MigrateDatabaseAsync();

app.UseForwardedHeaders();
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
api.MapAuth();
api.MapOrgs();
api.MapAccount();
api.MapMembers();
api.MapBilling();
api.MapMetrics();
api.MapProjects();
api.MapCredentials();

app.Run();

/// <summary>Pubblica solo perché <c>WebApplicationFactory&lt;Program&gt;</c> dei test ci si possa agganciare.</summary>
public partial class Program;
