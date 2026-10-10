using Flarelytics.Api.Email;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>
/// L'API vera su un database suo, con email e store finti e una cartella di
/// segreti temporanea.
/// </summary>
/// <param name="settings">Configurazione in più (Bunny, la modalità dello storage), uguale per l'API e per il <see cref="SyncHost"/>.</param>
public class FlarelyticsAppFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public IReadOnlyDictionary<string, string?> Settings { get; } = settings ?? new Dictionary<string, string?>();

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "flarelytics-tests", Guid.NewGuid().ToString("N"));
    public string KeysDirectory => Path.Combine(Root, "keys");
    public string SecretsDirectory => Path.Combine(Root, "secrets");

    public RecordingEmailSender Emails { get; } = new();
    public FakeStoreGateway AppStore { get; } = new(Store.AppStore);
    public FakeStoreGateway GooglePlay { get; } = new(Store.GooglePlay);

    /// <summary>Le API di gestione degli store (versioni, recensioni, pagina, build).</summary>
    public FakeStoreServer StoreApis { get; } = new();

    /// <summary>Le reti social: Bluesky, Mastodon, Graph API di Meta.</summary>
    public FakeStoreServer SocialApis { get; } = new();

    /// <summary>La storage zone di Bunny, per i file dei post (si usa se nelle <see cref="Settings"/> c'è Bunny).</summary>
    public FakeBunny Bunny { get; } = new();

    /// <summary>Servizi da sostituire per un test (prima di toccare <c>Services</c>).</summary>
    public Action<IServiceCollection>? TestServices { get; init; }

    /// <summary>I client che parlano con le reti social, da attaccare a <see cref="SocialApis"/>.</summary>
    public static readonly string[] SocialClients =
        [nameof(Flarelytics.Core.Social.BlueskyClient), nameof(Flarelytics.Core.Social.MastodonClient), nameof(Flarelytics.Core.Social.MetaGraphClient),
         nameof(Flarelytics.Core.Social.InstagramLoginClient), nameof(Flarelytics.Core.Social.RemoteImageClient),
         nameof(Flarelytics.Core.Social.TikTokClient), nameof(Flarelytics.Core.Social.ThreadsClient)];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        KeyRing.CreateKeyFile(KeysDirectory, "v1");

        builder.UseEnvironment("Testing");

        // UseSetting e non ConfigureAppConfiguration: con l'hosting minimale
        // la configurazione si legge mentre il builder si costruisce, e quella
        // aggiunta dopo arriverebbe tardi.
        builder.UseSetting("ConnectionStrings:Database", connectionString);
        builder.UseSetting("Jwt:Key", "chiave-dei-test-lunga-almeno-trentadue-caratteri");
        builder.UseSetting("Auth:SecureCookies", "false");
        builder.UseSetting("Auth:PublicAppUrl", "http://app.test");
        builder.UseSetting("Auth:AuthRequestsPerMinute", "10000");
        // Il worker non deve girare da solo durante i test: lo si chiama a mano (SyncHost).
        builder.UseSetting("Worker:Enabled", "false");
        builder.UseSetting("Secrets:KeysDirectory", KeysDirectory);
        builder.UseSetting("Secrets:StorageDirectory", SecretsDirectory);
        builder.UseSetting("Reports:StorageDirectory", Path.Combine(Root, "reports"));
        builder.UseSetting("Social:PollDelay", "00:00:00");
        builder.UseSetting("Social:Meta:AppId", "app-meta");
        builder.UseSetting("Social:Meta:AppSecret", "segreto-meta");
        builder.UseSetting("Social:Instagram:AppId", "app-instagram");
        builder.UseSetting("Social:Instagram:AppSecret", "segreto-instagram");
        builder.UseSetting("Social:TikTok:ClientKey", "chiave-tiktok");
        builder.UseSetting("Social:TikTok:ClientSecret", "segreto-tiktok");
        builder.UseSetting("Social:Threads:AppId", "app-threads");
        builder.UseSetting("Social:Threads:AppSecret", "segreto-threads");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);

            // I client di gestione parlano con il finto server invece che con
            // Apple e Google. Il nome del client tipizzato è quello del tipo.
            foreach (var client in new[] { nameof(Flarelytics.Core.Management.AppleApi), nameof(Flarelytics.Core.Management.GooglePublisher) })
            {
                services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(client,
                    o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = StoreApis));
            }

            foreach (var client in SocialClients)
            {
                services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(client,
                    o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = SocialApis));
            }

            services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(nameof(Flarelytics.Core.Social.Media.BunnyStorageClient),
                o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Bunny));

            services.RemoveAll<IStoreGateway>();
            services.AddSingleton<IStoreGateway>(AppStore);
            services.AddSingleton<IStoreGateway>(GooglePlay);
            TestServices?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // Le connessioni rimaste nei pool di questo test non servono più a nessuno.
        Npgsql.NpgsqlConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
