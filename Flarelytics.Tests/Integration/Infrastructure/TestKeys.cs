using System.Security.Cryptography;
using System.Text.Json;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>Chiavi vere, generate al momento, nella forma in cui le danno Apple e Google.</summary>
public static class TestKeys
{
    /// <summary>Un .p8 come quello di App Store Connect: EC P-256 in PKCS#8.</summary>
    public static string AppleP8()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    }

    public static string GoogleServiceAccountJson(string clientEmail = "flarelytics@progetto.iam.gserviceaccount.com")
    {
        using var rsa = RSA.Create(2048);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "progetto",
            ["private_key_id"] = Guid.NewGuid().ToString("N"),
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = clientEmail,
            ["token_uri"] = "https://oauth2.googleapis.com/token"
        });
    }

    public static object AppleRequest(string? p8 = null, string label = "Team Acme") => new
    {
        label,
        keyId = "ABCDE12345",
        issuerId = "69a6de7e-0000-47e3-e053-5b8c7c11a4d1",
        vendorNumber = "85012345",
        privateKey = p8 ?? AppleP8()
    };
}
