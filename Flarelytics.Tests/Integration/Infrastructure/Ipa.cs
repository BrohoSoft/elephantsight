using System.IO.Compression;
using System.Text;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>.ipa finti ma nel formato vero: uno zip con <c>Payload/&lt;App&gt;.app/Info.plist</c>.</summary>
public static class Ipa
{
    /// <summary>
    /// Un Info.plist binario generato con plistlib di Python: com.esempio.meteo,
    /// versione 2.1.0, build 137, con stringhe UTF-16 (il nome ha una è),
    /// un array e un booleano, che il lettore deve saltare.
    /// </summary>
    public static readonly byte[] BinaryPlist = Convert.FromBase64String(
        "YnBsaXN0MDDXAQIDBAUGBwgJCgsMDQ5fEBJDRkJ1bmRsZUlkZW50aWZpZXJcQ0ZCdW5kbGVOYW1lXxAaQ0ZCdW5kbGVTaG9ydFZlcnNpb25TdHJpbmdfEA9DRkJ1bmRsZVZlcnNpb25fEBJMU1JlcXVpcmVzSVBob25lT1NfEBBNaW5pbXVtT1NWZXJzaW9uXxAcVUlSZXF1aXJlZERldmljZUNhcGFiaWxpdGllc18QEWNvbS5lc2VtcGlvLm1ldGVvbQBNAGUAdABlAG8AIADoACAAYgBlAGwAbABvVTIuMS4wUzEzNwlUMTcuMKEPVWFybTY0CBcsOVZofZCvw97k6Onu8AAAAAAAAAEBAAAAAAAAABAAAAAAAAAAAAAAAAAAAAD2");

    public static byte[] XmlPlist(string bundleId, string version, string build) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleIdentifier</key><string>{bundleId}</string>
          <key>CFBundleShortVersionString</key><string>{version}</string>
          <key>CFBundleVersion</key><string>{build}</string>
          <key>UIRequiresFullScreen</key><true/>
        </dict></plist>
        """);

    public static byte[] Create(byte[] infoPlist)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("Payload/Meteo.app/Info.plist").Open()) s.Write(infoPlist);
            using (var s = zip.CreateEntry("Payload/Meteo.app/Meteo").Open()) s.Write(new byte[4096]);
        }
        return output.ToArray();
    }
}
