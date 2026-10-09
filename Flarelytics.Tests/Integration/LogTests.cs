using System.Net;
using System.Net.Http.Json;
using Flarelytics.Core.Database;
using Flarelytics.Core.Logging;
using Flarelytics.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Tests.Integration;

/// <summary>La pagina Log: ognuno vede i messaggi della sua organizzazione, solo un owner quelli di sistema.</summary>
[Trait("Category", "Integration")]
[Collection(DatabaseCollection.Name)]
public class LogTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FlarelyticsAppFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new FlarelyticsAppFactory(await postgres.CreateDatabaseAsync());
        _ = _app.Services;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task WriteAsync(params LogEntry[] entries) =>
        await _app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<StoredLogWriter>().Single().FlushAsync(entries);

    [Fact]
    public async Task Ognuno_vede_i_log_della_sua_organizzazione_e_l_owner_anche_quelli_di_sistema()
    {
        var a = await _app.SignUpAsync();
        var b = await _app.SignUpAsync();
        var now = DateTime.UtcNow;
        await WriteAsync(
            LogEntry.Create(now, LogLevel.Information, "Flarelytics.Core.Social.SocialPublisher", "Post pubblicato su Threads", null, a.OrgId),
            LogEntry.Create(now, LogLevel.Warning, "Flarelytics.Core.Social.SocialPublisher", "Post rifiutato da Threads: elementi non validi", null, a.OrgId),
            LogEntry.Create(now, LogLevel.Error, "Flarelytics.Core.Sync.AppleSalesSync", "Chiave Apple non valida", "System.Exception: 401", b.OrgId),
            LogEntry.Create(now, LogLevel.Error, "Microsoft.AspNetCore.Server.Kestrel", "Errore di sistema", null, null));

        var all = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs")).ReadJsonAsync();
        var messages = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message").GetString()).ToList();
        Assert.Equal(["Errore di sistema", "Post rifiutato da Threads: elementi non validi", "Post pubblicato su Threads"], messages);

        var warnings = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs?level=Warning&area=Social")).ReadJsonAsync();
        Assert.Equal("Post rifiutato da Threads: elementi non validi", Assert.Single(warnings.GetProperty("items").EnumerateArray()).GetProperty("message").GetString());

        var search = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs?q=PUBBLICATO")).ReadJsonAsync();
        Assert.Single(search.GetProperty("items").EnumerateArray());

        // Un admin che non è owner: la sua organizzazione sì, il sistema no.
        var admin = await _app.SignUpAsync();
        await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/invitations", new { email = admin.Email, role = "Admin" });
        await admin.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = _app.LinkToken(admin.Email) });
        var asAdmin = await (await admin.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs")).ReadJsonAsync();
        Assert.DoesNotContain(asAdmin.GetProperty("items").EnumerateArray(), i => i.GetProperty("system").GetBoolean());
        Assert.Equal(2, asAdmin.GetProperty("items").GetArrayLength());

        // Un lettore no.
        var viewer = await _app.SignUpAsync();
        await a.Client.PostAsJsonAsync($"/api/v1/orgs/{a.OrgId}/invitations", new { email = viewer.Email, role = "Viewer" });
        await viewer.Client.PostAsJsonAsync("/api/v1/invitations/accept", new { token = _app.LinkToken(viewer.Email) });
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs")).StatusCode);
    }

    [Fact]
    public async Task I_log_si_leggono_a_pagine_dal_piu_recente()
    {
        var a = await _app.SignUpAsync();
        await WriteAsync(Enumerable.Range(1, 130).Select(i => LogEntry.Create(DateTime.UtcNow, LogLevel.Information, "Flarelytics.Core.Social.X", $"messaggio {i}", null, a.OrgId)).ToArray());

        var first = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs?area=Social")).ReadJsonAsync();
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(100, items.Count);
        Assert.Equal("messaggio 130", items[0].GetProperty("message").GetString());

        var next = await (await a.Client.GetAsync($"/api/v1/orgs/{a.OrgId}/logs?area=Social&before={items[^1].GetProperty("id").GetInt64()}")).ReadJsonAsync();
        Assert.False(next.GetProperty("hasMore").GetBoolean());
        Assert.Equal(30, next.GetProperty("items").GetArrayLength());
    }
}
