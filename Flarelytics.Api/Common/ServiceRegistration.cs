using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Flarelytics.Api.Auth;
using Flarelytics.Api.Email;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core;
using Flarelytics.Core.Billing;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Flarelytics.Core.Tenancy;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Flarelytics.Api.Common;

public static class ServiceRegistration
{
    public static void ConfigureApi(this WebApplicationBuilder builder)
    {
        // Gli enum viaggiano come testo ("AppStore", "Owner"): un numero nel
        // JSON non dice niente a chi lo legge, e cambierebbe se si riordinasse
        // l'enum.
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
        builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
        builder.Services.AddOpenApi();
    }

    public static void ConfigureDatabase(this WebApplicationBuilder builder) =>
        builder.Services.AddFlarelyticsDatabase(builder.Configuration);

    public static void ConfigureAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations().ValidateOnStart();

        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddScoped<RefreshTokenService>();
        builder.Services.AddSingleton<RefreshCookie>();
        builder.Services.AddScoped<CurrentOrg>();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configurato dopo, dalle opzioni già validate, invece che leggendo la
        // configurazione a mano qui sopra.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((o, jwt) =>
            {
                // Lascia i claim con il loro nome: "sub" resta "sub", invece di
                // diventare l'URI di ClaimTypes.NameIdentifier.
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = TokenService.SigningKey(jwt.Value),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
                o.Events = new JwtBearerEvents { OnTokenValidated = RejectStaleStamp };
            });

        builder.Services.AddAuthorization();
    }

    /// <summary>
    /// Rifiuta i token emessi prima di un cambio password. La firma da sola non
    /// basta: un token rubato resterebbe buono fino alla scadenza anche dopo
    /// che il proprietario ha cambiato la password apposta.
    /// </summary>
    private static async Task RejectStaleStamp(TokenValidatedContext context)
    {
        var stamp = context.Principal?.FindFirst(TokenService.StampClaim)?.Value;
        var userId = context.Principal?.UserId();
        var db = context.HttpContext.RequestServices.GetRequiredService<FlarelyticsDbContext>();

        var current = await db.Set<User>().AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        if (current is null || current != stamp) context.Fail("Token non più valido.");
    }

    public static void ConfigureRateLimiting(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.RateLimitPolicy, http =>
            {
                var limit = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.AuthRequestsPerMinute;

                // Per IP. Dietro Caddy l'IP vero arriva dagli header inoltrati,
                // che UseForwardedHeaders ha già applicato a RemoteIpAddress.
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) });
            });
        });
    }

    /// <summary>
    /// SMTP se configurato, altrimenti il log. In produzione l'SMTP è
    /// obbligatorio: senza, nessuno riuscirebbe a confermare l'account, e lo si
    /// scoprirebbe alla prima registrazione invece che all'avvio.
    /// </summary>
    public static void ConfigureEmail(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AccountEmails>();

        if (builder.Environment.IsProduction() || builder.Configuration.GetSection(SmtpOptions.Section).Exists())
        {
            builder.Services.AddOptions<SmtpOptions>().BindConfiguration(SmtpOptions.Section).ValidateDataAnnotations().ValidateOnStart();
            builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            builder.Services.AddScoped<IEmailSender, LogEmailSender>();
        }
    }

    public static void ConfigureSecrets(this WebApplicationBuilder builder)
    {
        builder.Services.AddFlarelyticsSecretsAndStores(builder.Configuration, createDevelopmentKey: builder.Environment.IsDevelopment());
        builder.Services.AddScoped<TwoFactorService>();
    }

    public static void ConfigureBilling(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IBillingProvider, ManualBillingProvider>();
    }

    /// <summary>
    /// Porta lo schema all'ultima migration prima di servire richieste.
    /// </summary>
    /// <remarks>
    /// Con più istanze che partono insieme EF prende un lock sulla tabella
    /// della cronologia, quindi una sola applica le migration e le altre
    /// aspettano.
    /// </remarks>
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>().Database.MigrateAsync();

        // Prova subito che la chiave master si carica, invece che al primo
        // caricamento di una credenziale.
        scope.ServiceProvider.GetRequiredService<KeyRing>();
    }
}
