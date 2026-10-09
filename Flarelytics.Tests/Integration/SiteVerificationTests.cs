using System.Net;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>I file con cui le piattaforme verificano il sito (TikTok, Meta, Google).</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class SiteVerificationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Il_file_di_verifica_si_legge_sotto_il_prefisso_scelto_dalla_piattaforma()
    {
        var client = _app.CreateClient();
        var directory = Path.Combine(_app.Root, "reports", "_verify");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "tiktokAbC123.txt"), "tiktok-developers-site-verification=AbC123");

        foreach (var path in new[] { "/tiktokAbC123.txt", "/tiktock/verify/tiktokAbC123.txt" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("tiktok-developers-site-verification=AbC123", await response.Content.ReadAsStringAsync());
        }

        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/tiktock/verify/tiktokAltro.txt")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/x/..%2F..%2Fkeys%2Fv1.key")).StatusCode);
    }
}
