using System.Text;
using Flarelytics.Api.Auth;

namespace Flarelytics.Tests.Unit;

public class TotpTests
{
    // Il seme dei vettori di prova della RFC 6238, appendice B (SHA-1).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>
    /// I valori della RFC sono a 8 cifre: le ultime 6 sono quelle che dà
    /// qualunque app di autenticazione.
    /// </summary>
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Coincide_con_i_vettori_della_rfc(long unixSeconds, string expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.StepAt(now)));
    }

    [Fact]
    public void Accetta_l_intervallo_prima_e_dopo_ma_non_oltre()
    {
        var now = DateTime.UtcNow;
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 1), now, null));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null));
    }

    [Fact]
    public void Un_intervallo_gia_usato_non_vale_piu()
    {
        var now = DateTime.UtcNow;
        var step = Totp.StepAt(now);

        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: step));
    }

    [Fact]
    public void Base32_come_lo_leggono_le_app()
    {
        // RFC 4648: "foobar" → MZXW6YTBOI
        Assert.Equal("MZXW6YTBOI", Totp.Base32("foobar"u8));
    }
}
