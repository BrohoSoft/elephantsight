using System.Net;
using System.Net.Http.Json;
using Flarelytics.Api.Auth;
using Flarelytics.Tests.Integration.Infrastructure;

namespace Flarelytics.Tests.Integration;

/// <summary>Attivazione della 2FA e accesso in due passaggi.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class TwoFactorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync() => _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Attiva la 2FA come farebbe l'utente; restituisce il seme e i codici di recupero.</summary>
    private static async Task<(byte[] Secret, List<string> RecoveryCodes)> EnableAsync(Account account)
    {
        var setup = await (await account.Client.PostAsJsonAsync("/api/v1/me/2fa/setup", new { password = TestApi.Password })).ReadJsonAsync();
        var secret = Base32.Decode(setup.GetProperty("secret").GetString()!);
        Assert.StartsWith("otpauth://totp/Flarelytics:", setup.GetProperty("otpAuthUri").GetString());

        var code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow));
        var enabled = await account.Client.PostAsJsonAsync("/api/v1/me/2fa/enable", new { code });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        var codes = (await enabled.ReadJsonAsync()).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        return (secret, codes);
    }

    private async Task<string> ChallengeAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestApi.Password });
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);

        var body = await login.ReadJsonAsync();
        Assert.True(body.GetProperty("twoFactorRequired").GetBoolean());
        Assert.False(body.TryGetProperty("accessToken", out _));
        return body.GetProperty("challengeToken").GetString()!;
    }

    [Fact]
    public async Task Con_la_2fa_la_password_da_sola_non_apre_la_sessione()
    {
        var account = await _app.SignUpAsync();
        var (secret, _) = await EnableAsync(account);

        var client = _app.CreateClient();
        var challenge = await ChallengeAsync(client, account.Email);

        // Senza codice nessun cookie di sessione.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/auth/refresh", null)).StatusCode);

        // L'intervallo successivo: quello corrente è già stato speso per l'attivazione.
        var code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow) + 1);
        var session = await client.PostAsJsonAsync("/api/v1/auth/login/2fa", new { challengeToken = challenge, code });

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.False(string.IsNullOrEmpty((await session.ReadJsonAsync()).GetProperty("accessToken").GetString()));
    }

    [Fact]
    public async Task Lo_stesso_codice_non_vale_due_volte()
    {
        var account = await _app.SignUpAsync();
        var (secret, _) = await EnableAsync(account);
        var code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow) + 1);

        var client = _app.CreateClient();
        var first = await client.PostAsJsonAsync("/api/v1/auth/login/2fa", new { challengeToken = await ChallengeAsync(client, account.Email), code });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await client.PostAsJsonAsync("/api/v1/auth/login/2fa", new { challengeToken = await ChallengeAsync(client, account.Email), code });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Un_codice_di_recupero_funziona_una_volta_sola()
    {
        var account = await _app.SignUpAsync();
        var (_, recovery) = await EnableAsync(account);
        var client = _app.CreateClient();

        var first = await client.PostAsJsonAsync("/api/v1/auth/login/2fa",
            new { challengeToken = await ChallengeAsync(client, account.Email), code = recovery[0].ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var again = await client.PostAsJsonAsync("/api/v1/auth/login/2fa",
            new { challengeToken = await ChallengeAsync(client, account.Email), code = recovery[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task Dopo_cinque_errori_la_sfida_si_brucia()
    {
        var account = await _app.SignUpAsync();
        var (secret, _) = await EnableAsync(account);
        var client = _app.CreateClient();
        var challenge = await ChallengeAsync(client, account.Email);

        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostAsJsonAsync("/api/v1/auth/login/2fa", new { challengeToken = challenge, code = "000000" });
            Assert.Equal("invalid_code", await wrong.ProblemCodeAsync());
        }

        // Anche il codice giusto, ora, non basta più: serve rifare il login.
        var right = await client.PostAsJsonAsync("/api/v1/auth/login/2fa",
            new { challengeToken = challenge, code = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow) + 1) });
        Assert.Equal("challenge_expired", await right.ProblemCodeAsync());
    }

    [Fact]
    public async Task Per_disattivarla_servono_password_e_codice()
    {
        var account = await _app.SignUpAsync();
        var (_, recovery) = await EnableAsync(account);

        var noCode = await account.Client.PostAsJsonAsync("/api/v1/me/2fa/disable", new { password = TestApi.Password, code = "000000" });
        Assert.Equal("invalid_code", await noCode.ProblemCodeAsync());

        var ok = await account.Client.PostAsJsonAsync("/api/v1/me/2fa/disable", new { password = TestApi.Password, code = recovery[1] });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var me = await (await account.Client.GetAsync("/api/v1/me")).ReadJsonAsync();
        Assert.False(me.GetProperty("twoFactorEnabled").GetBoolean());

        // E il login torna a un passo solo.
        Assert.Equal(HttpStatusCode.OK, (await _app.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = account.Email, password = TestApi.Password })).StatusCode);
    }

    [Fact]
    public async Task Il_seme_a_database_e_cifrato()
    {
        var account = await _app.SignUpAsync();
        var (secret, _) = await EnableAsync(account);

        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_app.Services);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Flarelytics.Core.Database.FlarelyticsDbContext>(scope.ServiceProvider);
        var stored = db.Set<Flarelytics.Core.Database.Entities.User>().Single(u => u.Email == account.Email).TotpSecretProtected!;

        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain(Totp.Base32(secret), stored);
    }
}

internal static class Base32
{
    public static byte[] Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;

        foreach (var c in input.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
