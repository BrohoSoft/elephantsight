using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Flarelytics.Api.Auth;
using Flarelytics.Api.Email;
using Flarelytics.Api.Features.Auth;
using Flarelytics.Api.Features.Orgs;
using Flarelytics.Core;
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
        MoveOldJwtKey(builder.Configuration);
        builder.Services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Section)
            .PostConfigure(o => o.Key = string.IsNullOrWhiteSpace(o.Key) ? GeneratedJwtKey(builder.Configuration) ?? o.Key : o.Key)
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations().ValidateOnStart();

        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddScoped<RefreshTokenService>();
        builder.Services.AddSingleton<RefreshCookie>();
        builder.Services.AddScoped<CurrentOrg>();
        builder.Services.AddScoped<Features.PublicApi.CurrentApiKey>();

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
                o.Events = new JwtBearerEvents
                {
                    OnTokenValidated = RejectStaleStamp,
                    // Le chiavi API ("Bearer wsk_…") non sono JWT: le controlla
                    // ApiKeyFilter sulle rotte /public. Senza questo il gestore
                    // proverebbe a leggerle come token e riempirebbe il log.
                    OnMessageReceived = c =>
                    {
                        if (Features.PublicApi.ApiKeyFilter.ReadKey(c.Request) is not null) c.NoResult();
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();
    }

    // Non ".key": KeyRing prende ogni *.key della cartella come versione della
    // chiave master, e questa (48 byte) gli impedirebbe di partire.
    private const string JwtKeyFile = "jwt.secret";

    /// <summary>
    /// Le prime versioni la chiamavano jwt.key, che dal secondo avvio bloccava
    /// KeyRing. Si rinomina subito, prima che qualcuno costruisca KeyRing:
    /// la generazione della chiave invece è pigra e arriverebbe tardi.
    /// </summary>
    private static void MoveOldJwtKey(IConfiguration configuration)
    {
        var directory = configuration["Secrets:KeysDirectory"];
        if (string.IsNullOrWhiteSpace(directory)) return;

        var oldPath = Path.Combine(directory, "jwt.key");
        var path = Path.Combine(directory, JwtKeyFile);
        if (File.Exists(oldPath) && !File.Exists(path)) File.Move(oldPath, path);
    }

    /// <summary>
    /// La chiave di firma dei token, quando la configurazione non la dà:
    /// generata al primo avvio accanto alla chiave master, così chi installa
    /// non deve inventarne una. Solo con <c>Secrets:CreateKeyIfMissing</c>.
    /// </summary>
    /// <remarks>
    /// Se il file si perde, tutti dovranno rifare il login, ma non si perde
    /// niente: per questo, a differenza della chiave master, rigenerarlo è
    /// innocuo.
    /// </remarks>
    private static string? GeneratedJwtKey(IConfiguration configuration)
    {
        var directory = configuration["Secrets:KeysDirectory"];
        if (string.IsNullOrWhiteSpace(directory) || !configuration.GetValue("Secrets:CreateKeyIfMissing", false)) return null;

        var path = Path.Combine(directory, JwtKeyFile);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return File.ReadAllText(path).Trim();
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
            o.AddPolicy(Features.PublicApi.ApiKeyFilter.RateLimitPolicy, Features.PublicApi.ApiKeyFilter.Partition);
        });
    }

    /// <summary>
    /// SMTP se configurato, altrimenti niente email. È facoltativo: senza, gli
    /// inviti si mandano copiando il link dal pannello e il recupero password
    /// non è disponibile (il pannello lo sa da <c>/instance</c>).
    /// </summary>
    public static void ConfigureEmail(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AccountEmails>();

        if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{SmtpOptions.Section}:Host"]))
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
        // Crea la chiave master se manca: comodo in sviluppo e al primo avvio
        // del container, dove chi installa non deve generarla a mano. Il log
        // all'avvio ricorda di salvarla fuori dal server.
        builder.Services.AddFlarelyticsSecretsAndStores(builder.Configuration,
            createDevelopmentKey: builder.Configuration.GetValue("Secrets:CreateKeyIfMissing", builder.Environment.IsDevelopment()));
        builder.Services.AddFlarelyticsSocial();
        builder.Services.AddScoped<TwoFactorService>();
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
