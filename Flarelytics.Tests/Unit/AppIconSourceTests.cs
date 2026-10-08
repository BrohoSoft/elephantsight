using Flarelytics.Core.Reports;

namespace Flarelytics.Tests.Unit;

public class AppIconSourceTests
{
    [Fact]
    public void L_icona_di_google_play_si_legge_dal_meta_og_image_a_256_pixel()
    {
        const string html = """<html><head><meta property="og:image" content="https://play-lh.googleusercontent.com/AbC_dEf123=w526-h296&amp;rw"></head></html>""";
        Assert.Equal("https://play-lh.googleusercontent.com/AbC_dEf123=s256", AppIconSource.GoogleIconUrl(html));
    }

    [Fact]
    public void Senza_suffisso_di_dimensione_lo_aggiunge()
    {
        const string html = """<meta content="x" property="og:title"><meta property='og:image' content='https://play-lh.googleusercontent.com/XyZ'>""";
        Assert.Equal("https://play-lh.googleusercontent.com/XyZ=s256", AppIconSource.GoogleIconUrl(html));
    }

    [Theory]
    [InlineData("<html><head><title>Not found</title></head></html>")]
    [InlineData("""<meta property="og:image" content="http://insicuro.example/icona.png">""")]
    public void Senza_un_immagine_https_non_c_e_icona(string html) => Assert.Null(AppIconSource.GoogleIconUrl(html));
}
