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

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);

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
