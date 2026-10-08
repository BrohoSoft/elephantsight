using Flarelytics.Core.Management;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Unit;

public class IpaReaderTests
{
    [Fact]
    public void Legge_l_info_plist_binario_che_scrive_xcode()
    {
        var info = IpaReader.Read(new MemoryStream(Ipa.Create(Ipa.BinaryPlist)));
        Assert.Equal(new IpaInfo("com.esempio.meteo", "2.1.0", "137"), info);
    }

    [Fact]
    public void Legge_anche_l_info_plist_xml()
    {
        var info = IpaReader.Read(new MemoryStream(Ipa.Create(Ipa.XmlPlist("com.esempio.app", "1.0", "5"))));
        Assert.Equal(new IpaInfo("com.esempio.app", "1.0", "5"), info);
    }

    [Fact]
    public void Le_stringhe_utf16_del_plist_binario_si_leggono()
    {
        Assert.Equal("Meteo è bello", IpaReader.ParsePlist(Ipa.BinaryPlist)["CFBundleName"]);
    }

    [Fact]
    public void Uno_zip_che_non_e_un_ipa_si_rifiuta()
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var s = zip.CreateEntry("documento.txt").Open();
            s.Write("ciao"u8);
        }
        output.Position = 0;
        Assert.Throws<FormatException>(() => IpaReader.Read(output));
    }
}
