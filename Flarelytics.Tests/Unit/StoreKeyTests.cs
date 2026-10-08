using System.Security.Cryptography;
using Flarelytics.Core.Stores;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Flarelytics.Tests.Unit;

public class StoreKeyTests
{
    private const string KeyId = "ABCDE12345";
    private const string IssuerId = "69a6de7e-0000-47e3-e053-5b8c7c11a4d1";

    [Fact]
    public void Il_jwt_per_apple_ha_quello_che_apple_chiede()
    {
        var key = AppleKey.Parse(KeyId, IssuerId, null, TestKeys.AppleP8());
        var now = DateTime.UtcNow;

        var token = new JsonWebToken(AppStoreConnectGateway.CreateToken(key, now));

        Assert.Equal("ES256", token.Alg);
        Assert.Equal(KeyId, token.Kid);
        Assert.Equal(IssuerId, token.Issuer);
        Assert.Equal("appstoreconnect-v1", token.Audiences.Single());

        // Apple rifiuta i token che durano più di 20 minuti.
        Assert.True(token.ValidTo - token.IssuedAt <= TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Il_jwt_si_verifica_con_la_chiave_pubblica()
    {
        var p8 = TestKeys.AppleP8();
        var token = new JsonWebToken(AppStoreConnectGateway.CreateToken(AppleKey.Parse(KeyId, IssuerId, null, p8), DateTime.UtcNow));

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(p8);
        var signed = System.Text.Encoding.ASCII.GetBytes(token.EncodedHeader + "." + token.EncodedPayload);
        var signature = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(token.EncodedSignature);

        // 64 byte, R||S: il formato JWS. La firma DER sarebbe più lunga e Apple la rifiuterebbe.
        Assert.Equal(64, signature.Length);
        Assert.True(ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// Il worker firma un token per ogni richiesta, con la stessa chiave. La
    /// cache di IdentityModel riusava l'oggetto della firma precedente, già
    /// eliminato: il primo token andava, il secondo esplodeva.
    /// </summary>
    [Fact]
    public void Si_firmano_piu_token_di_fila_con_la_stessa_chiave()
    {
        var key = AppleKey.Parse(KeyId, IssuerId, null, TestKeys.AppleP8());

        for (var i = 0; i < 3; i++)
        {
            Assert.NotEmpty(AppStoreConnectGateway.CreateToken(key, DateTime.UtcNow));
        }
    }

    [Fact]
    public void Una_chiave_rsa_non_passa_per_un_p8_di_apple()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<InvalidStoreKeyException>(() => AppleKey.Parse(KeyId, IssuerId, null, rsa.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void Una_chiave_ec_su_un_altra_curva_non_passa()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidStoreKeyException>(() => AppleKey.Parse(KeyId, IssuerId, null, ecdsa.ExportPkcs8PrivateKeyPem()));
    }

    [Theory]
    [InlineData("abcde12345")]
    [InlineData("ABCDE1234")]
    public void Key_id_malformato(string keyId) =>
        Assert.Throws<InvalidStoreKeyException>(() => AppleKey.Parse(keyId, IssuerId, null, TestKeys.AppleP8()));

    [Fact]
    public void Un_json_che_non_e_un_service_account_si_rifiuta()
    {
        Assert.Throws<InvalidStoreKeyException>(() => GoogleServiceAccount.Parse("""{"type":"authorized_user"}"""));
        Assert.Throws<InvalidStoreKeyException>(() => GoogleServiceAccount.Parse("non json"));
    }

    [Fact]
    public void Un_token_uri_diverso_da_quello_di_google_si_rifiuta()
    {
        var json = TestKeys.GoogleServiceAccountJson().Replace("https://oauth2.googleapis.com/token", "https://attaccante.example/token");
        Assert.Throws<InvalidStoreKeyException>(() => GoogleServiceAccount.Parse(json));
    }

    [Fact]
    public void L_impronta_e_stabile_e_distingue_le_chiavi()
    {
        var p8 = TestKeys.AppleP8();
        var a = AppleKey.Parse(KeyId, IssuerId, null, p8).Fingerprint();

        Assert.Equal(a, AppleKey.Parse(KeyId, IssuerId, null, p8).Fingerprint());
        Assert.NotEqual(a, AppleKey.Parse(KeyId, IssuerId, null, TestKeys.AppleP8()).Fingerprint());
    }
}
