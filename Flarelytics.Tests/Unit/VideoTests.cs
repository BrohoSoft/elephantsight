using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Social;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Unit;

public class VideoTests
{
    private static VideoInfo? Read(byte[] file) => Mp4Info.TryRead(new MemoryStream(file));

    [Fact]
    public void Si_leggono_durata_dimensioni_e_posizione_dell_indice()
    {
        var info = Read(TestVideo.Create(1080, 1920, 15_500));
        Assert.Equal(new VideoInfo(1080, 1920, 15_500, FastStart: true, "video/mp4"), info);
        Assert.False(Read(TestVideo.Create(1080, 1920, 15_500, fastStart: false))!.FastStart);
        Assert.Equal("video/quicktime", Read(TestVideo.Create(1080, 1920, 1000, brand: "qt  "))!.ContentType);
    }

    [Fact]
    public void Un_video_verticale_salvato_ruotato_risulta_verticale()
    {
        var info = Read(TestVideo.Create(1080, 1920, 5000, rotated: true))!;
        Assert.Equal((1080, 1920), (info.Width, info.Height));
    }

    [Fact]
    public void Un_file_che_non_e_un_mp4_non_si_legge()
    {
        Assert.Null(Read([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6]));
        Assert.Null(Read(new byte[3]));
    }

    [Theory]
    [InlineData(3_000_000L, 3_000_000L, 1)]          // piccolo: un pezzo solo
    [InlineData(64L * 1024 * 1024, 64L * 1024 * 1024, 1)]
    [InlineData(250_000_000L, 10_000_000L, 25)]       // pezzi da 10 MB
    [InlineData(255_000_000L, 10_000_000L, 25)]       // l'ultimo si prende i 5 MB in più
    public void I_pezzi_per_TikTok_seguono_le_sue_regole(long size, long chunk, int count) =>
        Assert.Equal((chunk, count), TikTokClient.Chunks(size));

    private static SocialMedia Video(int seconds, bool fastStart = true) =>
        SocialMedia.CreateVideo(Guid.NewGuid(), "v.mp4", 5_000_000, new VideoInfo(1080, 1920, seconds * 1000, fastStart, "video/mp4"), Guid.NewGuid());

    [Fact]
    public void Un_video_e_un_Reel_su_Instagram_e_non_va_su_Bluesky()
    {
        Assert.Empty(SocialRules.Problems("Reel", [Video(30)], SocialRules.For(SocialNetwork.Instagram)));
        Assert.Contains(SocialRules.Problems("Reel", [Video(30, fastStart: false)], SocialRules.For(SocialNetwork.Instagram)), p => p.Contains("faststart"));
        Assert.Contains(SocialRules.Problems("Reel", [Video(2)], SocialRules.For(SocialNetwork.Instagram)), p => p.Contains("minimo"));
        Assert.Contains(SocialRules.Problems("Reel", [Video(30)], SocialRules.For(SocialNetwork.Bluesky)), p => p.Contains("non riceve video"));
        var image = SocialMedia.Create(Guid.NewGuid(), "a.jpg", 1000, 1080, 1080, Guid.NewGuid());
        Assert.Contains(SocialRules.Problems("Misto", [Video(30), image], SocialRules.For(SocialNetwork.Instagram)), p => p.Contains("da solo"));
    }

    [Fact]
    public void TikTok_vuole_un_video_e_la_visibilita_scelta_dalla_persona()
    {
        var image = SocialMedia.Create(Guid.NewGuid(), "a.jpg", 1000, 1080, 1080, Guid.NewGuid());
        Assert.Contains(SocialRules.Problems("Foto", [image], SocialRules.For(SocialNetwork.TikTok)), p => p.Contains("serve un video"));
        Assert.Contains(SocialRules.OptionProblems(SocialNetwork.TikTok, new PostOptions()), p => p.Contains("chi può vedere"));
        Assert.Empty(SocialRules.OptionProblems(SocialNetwork.TikTok, new PostOptions(TikTokPrivacy: "SELF_ONLY")));
        Assert.Contains(SocialRules.OptionProblems(SocialNetwork.TikTok, new PostOptions(TikTokPrivacy: "SELF_ONLY", TikTokBrandedContent: true)),
            p => p.Contains("partnership"));
        Assert.Empty(SocialRules.OptionProblems(SocialNetwork.Instagram, new PostOptions()));
    }
}
