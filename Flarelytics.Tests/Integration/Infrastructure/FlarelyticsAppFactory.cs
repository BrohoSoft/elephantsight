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
public class FlarelyticsAppFactory(string connectionString) : WebApplicationFactory<Program>
{
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

    /// <summary>I client che parlano con le reti social, da attaccare a <see cref="SocialApis"/>.</summary>
    public static readonly string[] SocialClients =
        [nameof(Flarelytics.Core.Social.BlueskyClient), nameof(Flarelytics.Core.Social.MastodonClient), nameof(Flarelytics.Core.Social.MetaGraphClient)];

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

            services.RemoveAll<IStoreGateway>();
            services.AddSingleton<IStoreGateway>(AppStore);
            services.AddSingleton<IStoreGateway>(GooglePlay);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
