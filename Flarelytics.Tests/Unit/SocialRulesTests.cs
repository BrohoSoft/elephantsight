using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Social;
using Flarelytics.Tests.Integration;
using Microsoft.Extensions.Options;

namespace Flarelytics.Tests.Unit;

public class SocialRulesTests
{
    [Fact]
    public void Bluesky_conta_i_grafemi_e_non_i_caratteri()
    {
        // Una bandiera sono due code point e quattro unità UTF-16, ma un grafema solo.
        Assert.Equal(3, SocialRules.Count("🇮🇹ok", SocialRules.For(SocialNetwork.Bluesky)));
        Assert.Equal(4, SocialRules.Count("🇮🇹ok", SocialRules.For(SocialNetwork.Instagram)));
    }

    [Fact]
    public void Mastodon_conta_ogni_link_come_23_caratteri()
    {
        var limits = SocialRules.For(SocialNetwork.Mastodon);
        Assert.Equal(4 + 23, SocialRules.Count("Qui https://esempio.it/una/pagina/molto/lunga/davvero", limits));
        Assert.Equal(500, limits.MaxCharacters);
        Assert.Equal(1000, SocialRules.For(SocialNetwork.Mastodon, 1000).MaxCharacters);
    }

    [Fact]
    public void Instagram_vuole_al_massimo_30_hashtag()
    {
        var text = string.Join(' ', Enumerable.Range(1, 31).Select(i => $"#tag{i}"));
        var image = SocialMedia.Create(Guid.NewGuid(), "a.jpg", 1000, 1080, 1080, Guid.NewGuid());
        Assert.Contains(SocialRules.Problems(text, [image], SocialRules.For(SocialNetwork.Instagram)), p => p.Contains("hashtag"));
        Assert.Empty(SocialRules.Problems("#uno #due", [image], SocialRules.For(SocialNetwork.Instagram)));
    }

    [Fact]
    public void I_numeri_non_sono_hashtag_per_Bluesky()
    {
        var facets = BlueskyClient.Facets("Versione #2 #meteo");
        Assert.Single(facets);
        Assert.Equal("meteo", facets[0]!["features"]![0]!["tag"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1080, 1350)]
    [InlineData(4032, 3024)]
    public void Le_dimensioni_del_jpeg_si_leggono_dall_intestazione(int width, int height)
    {
        Assert.True(JpegInfo.TryReadSize(TestJpeg.Create(width, height), out var w, out var h));
        Assert.Equal((width, height), (w, h));
        Assert.False(JpegInfo.TryReadSize([0x89, 0x50, 0x4E, 0x47], out _, out _));
    }

    [Fact]
    public void Un_indirizzo_firmato_vale_solo_per_la_sua_immagine_e_fino_alla_scadenza()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flarelytics-tests", Guid.NewGuid().ToString("N"));
        KeyRing.CreateKeyFile(directory, "v1");
        try
        {
            var signer = new MediaUrlSigner(new KeyRing(Options.Create(new SecretsOptions { KeysDirectory = directory, ActiveKeyVersion = "v1" })));
            var (tenant, media) = (Guid.NewGuid(), Guid.NewGuid());
            var path = new Uri("http://x" + signer.PathFor(tenant, media, DateTime.UtcNow.AddHours(1)));
            var id = path.Segments[^1][..^4];
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(path.Query);
            var (e, s) = (long.Parse(query["e"]!), query["s"].ToString());

            Assert.True(signer.TryVerify(id, e, s, DateTime.UtcNow, out var t, out var m));
            Assert.Equal((tenant, media), (t, m));
            Assert.False(signer.TryVerify(id, e + 3600, s, DateTime.UtcNow, out _, out _));            // scadenza allungata
            Assert.False(signer.TryVerify(id[..32] + Guid.NewGuid().ToString("N"), e, s, DateTime.UtcNow, out _, out _)); // altra immagine
            Assert.False(signer.TryVerify(id, e, s, DateTime.UtcNow.AddHours(2), out _, out _));        // scaduto
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
