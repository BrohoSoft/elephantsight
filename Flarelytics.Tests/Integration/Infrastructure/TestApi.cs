using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Un utente con la sua organizzazione, già dentro: ne esce un client con
    /// il bearer impostato.
    /// </summary>
    /// <remarks>
    /// Non c'è registrazione libera: il primo utente lo crea l'installer, gli
    /// altri entrano per invito. Per i test, che hanno bisogno di tante
    /// organizzazioni separate, l'utente si crea direttamente nel database
    /// (come farebbe l'installer) e poi entra dall'API come chiunque.
    /// </remarks>
    public static async Task<Account> SignUpAsync(this FlarelyticsAppFactory app, string? email = null)
    {
        email ??= $"u{Guid.NewGuid():N}@example.com";
        Guid orgId;

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlarelyticsDbContext>();
            var user = User.Create(email, Password, "Mario Rossi");
            user.ConfirmEmail(DateTime.UtcNow);
            var org = Tenant.Create("Acme " + email);
            db.AddRange(user, org, Membership.Create(org.Id, user.Id, OrgRole.Owner));
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        var client = app.CreateClient();
        await client.LoginAsync(email);
        return new Account(email, orgId, client);
    }

    public static async Task<string> LoginAsync(this HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

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
