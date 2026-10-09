using Flarelytics.Core.Database;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Flarelytics.Core.Sync;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flarelytics.Core;

/// <summary>
/// La registrazione dei servizi condivisi fra API e worker, in un punto solo:
/// se i due processi configurassero il database o le chiavi ciascuno a modo
/// suo, prima o poi uno dei due dimenticherebbe l'interceptor del tenant.
/// </summary>
public static class CoreServices
{
    /// <summary>
    /// Il database, con il tenant scritto su ogni connessione aperta.
    /// <see cref="TenantContext"/> e l'interceptor sono scoped come il
    /// DbContext: ognuno legge il tenant della propria richiesta o del proprio job.
    /// </summary>
    public static IServiceCollection AddFlarelyticsDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("Manca ConnectionStrings:Database.");

        services.AddScoped<TenantContext>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddDbContext<FlarelyticsDbContext>((sp, o) => o
            .UseNpgsql(connection)
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

        return services;
    }

    /// <summary>
    /// Chiavi master, file cifrati e client degli store.
    /// </summary>
    /// <param name="createDevelopmentKey">
    /// Crea la chiave master se manca. <b>Solo in sviluppo</b>: in produzione
    /// una chiave generata in silenzio finirebbe nello stesso posto dei dati, e
    /// perderla vorrebbe dire perdere tutte le credenziali.
    /// </param>
    public static IServiceCollection AddFlarelyticsSecretsAndStores(
        this IServiceCollection services, IConfiguration configuration, bool createDevelopmentKey)
    {
        services.AddOptions<SecretsOptions>().BindConfiguration(SecretsOptions.Section);

        if (createDevelopmentKey)
        {
            var o = configuration.GetSection(SecretsOptions.Section).Get<SecretsOptions>()!;
            KeyRing.CreateKeyFile(o.KeysDirectory, o.ActiveKeyVersion);
        }

        services.AddSingleton<KeyRing>();
        services.AddSingleton<SecretVault>();
        services.AddSingleton<CredentialSecrets>();
        services.AddSingleton<FieldProtector>();

        services.AddHttpClient<AppStoreConnectGateway>(c =>
        {
            c.BaseAddress = new Uri(AppStoreConnectGateway.BaseAddress);
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<GooglePlayGateway>();
        services.AddScoped<IStoreGateway>(sp => sp.GetRequiredService<AppStoreConnectGateway>());
        services.AddScoped<IStoreGateway>(sp => sp.GetRequiredService<GooglePlayGateway>());
        services.AddScoped<StoreGateways>();

        services.AddOptions<ReportsOptions>().BindConfiguration(ReportsOptions.Section);
        services.AddSingleton<ReportStorage>();
        services.AddSingleton<IconStorage>();

        // I report sono file anche di qualche MB: un minuto di margine.
        services.AddHttpClient<IAppleSalesReports, AppleSalesReports>(c =>
        {
            c.BaseAddress = new Uri(AppStoreConnectGateway.BaseAddress);
            c.Timeout = TimeSpan.FromSeconds(60);
        });

        // Gestione delle app: tempi lunghi perché qui passano anche i
        // caricamenti delle build, che pesano centinaia di MB.
        services.AddHttpClient<Management.AppleApi>(c =>
        {
            c.BaseAddress = new Uri(AppStoreConnectGateway.BaseAddress);
            c.Timeout = TimeSpan.FromMinutes(30);
        });
        services.AddHttpClient<Management.GooglePublisher>(c => c.Timeout = TimeSpan.FromMinutes(30));
        services.AddScoped<Management.ReleasesService>();
        services.AddScoped<Management.ReviewsService>();
        services.AddScoped<Management.ListingService>();
        services.AddSingleton<Management.UploadStorage>();
        services.AddScoped<Management.BuildUploader>();

        // I file mensili di Google sono di qualche centinaio di KB al massimo.
        services.AddHttpClient<IGooglePlayReports, GooglePlayReports>(c => c.Timeout = TimeSpan.FromSeconds(60));

        return services;
    }

    /// <summary>
    /// La pubblicazione sui social: client delle reti, immagini, firma degli
    /// indirizzi. Il ciclo che pubblica sta in <see cref="AddFlarelyticsSync"/>.
    /// </summary>
    public static IServiceCollection AddFlarelyticsSocial(this IServiceCollection services)
    {
        services.AddOptions<Social.SocialOptions>().BindConfiguration(Social.SocialOptions.Section)
            .PostConfigure<IConfiguration>((o, c) =>
            {
                o.AppUrl = (c["Auth:PublicAppUrl"] ?? "").TrimEnd('/');
                o.PublicUrl = string.IsNullOrWhiteSpace(o.PublicUrl) ? o.AppUrl : o.PublicUrl.TrimEnd('/');
            });
        services.AddSingleton<Social.SocialMediaStorage>();
        services.AddSingleton<Social.MediaUrlSigner>();
        services.AddHttpClient<Social.BlueskyClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<Social.MastodonClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<Social.MetaGraphClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<Social.InstagramLoginClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<Social.RemoteImageClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<Social.SocialPublisher>();
        services.AddScoped<Social.SocialImporter>();
        return services;
    }

    /// <summary>La sincronizzazione con gli store, con il ciclo in background che la fa girare.</summary>
    public static IServiceCollection AddFlarelyticsSync(this IServiceCollection services)
    {
        services.AddOptions<SyncOptions>().BindConfiguration(SyncOptions.Section);
        services.AddHttpClient<EcbExchangeRates>(c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<RatesRefresher>();
        services.AddScoped<ReportProcessor>();
        services.AddScoped<AppleSalesSync>();
        services.AddScoped<GooglePlaySync>();
        services.AddScoped<IconRefresher>();
        services.AddHttpClient<IAppIconSource, AppIconSource>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            // La pagina di Google Play risponde in modo diverso a chi non si
            // presenta come un browser.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WatchStore/1.0)");
        });
        services.AddSingleton<SyncCoordinator>();
        services.AddHostedService<SyncWorker>();
        services.AddHostedService<Management.BuildUploadWorker>();
        services.AddHostedService<Social.SocialPublishWorker>();
        return services;
    }
}
