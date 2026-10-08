using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flarelytics.Tests.Integration.Infrastructure;

/// <summary>Un utente con la sessione aperta e la sua organizzazione.</summary>
public record Account(string Email, Guid OrgId, HttpClient Client);

/// <summary>Le chiamate ricorrenti dei test, per tenerli concentrati su quello che verificano.</summary>
public static partial class TestApi
{
    public const string Password = "password-sicura-123";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Registrazione, conferma e accesso: ne esce un client con il bearer impostato.</summary>
    public static async Task<Account> SignUpAsync(this FlarelyticsAppFactory app, string? email = null)
    {
        email ??= $"u{Guid.NewGuid():N}@example.com";
        var client = app.CreateClient();

        var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = Password, fullName = "Mario Rossi", organizationName = "Acme " + email
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();

        var confirmed = await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { token = app.ConfirmationToken(email) });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        await client.LoginAsync(email);
        return new Account(email, body.GetProperty("organizationId").GetGuid(), client);
    }

    public static async Task<string> LoginAsync(this HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    public static string ConfirmationToken(this FlarelyticsAppFactory app, string email) => app.LinkToken(email);

    /// <summary>Il token del link nell'ultima email arrivata a quell'indirizzo.</summary>
    public static string LinkToken(this FlarelyticsAppFactory app, string email) =>
        Uri.UnescapeDataString(TokenInLink().Match(app.Emails.LastTo(email).Body).Groups[1].Value);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    [GeneratedRegex(@"token=(\S+)")]
    private static partial Regex TokenInLink();
}
