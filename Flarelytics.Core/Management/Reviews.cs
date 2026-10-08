using System.Text.Json.Nodes;
using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Management;

/// <summary>
/// Scarica le recensioni dagli store e manda le risposte.
/// </summary>
/// <remarks>Lavora sul tenant già impostato nello scope.</remarks>
public class ReviewsService(FlarelyticsDbContext db, AppleApi apple, GooglePublisher google, CredentialSecrets secrets, ILogger<ReviewsService> log)
{
    /// <summary>
    /// Alla prima sincronizzazione di un'app si risale lo storico (Apple lo dà
    /// tutto, a pagine da 200); dopo basta la prima pagina, perché le
    /// recensioni arrivano dalla più recente.
    /// </summary>
    private const int FirstSyncPages = 10;

    public async Task SyncAsync(StoreCredential credential, string appId, CancellationToken ct)
    {
        var state = await db.Set<ReviewSyncState>().SingleOrDefaultAsync(s => s.Store == credential.Store && s.AppId == appId, ct);
        if (state is null)
        {
            state = new ReviewSyncState { TenantId = credential.TenantId, Store = credential.Store, AppId = appId };
            db.Add(state);
        }

        try
        {
            var pages = state.LastSyncedAtUtc is null ? FirstSyncPages : 1;
            await secrets.UseAsync(credential, async secret =>
            {
                if (credential.Store == Store.AppStore) await SyncAppleAsync(apple.Open(credential, secret), credential.TenantId, appId, pages, ct);
                else await SyncGoogleAsync(await google.OpenAsync(secret, ct), credential.TenantId, appId, ct);
                return true;
            }, ct);

            state.LastSyncedAtUtc = DateTime.UtcNow;
            state.LastError = null;
        }
        catch (Exception e) when (e is StoreAccessException or HttpRequestException)
        {
            log.LogWarning("Recensioni di {Store} {App} non scaricate: {Message}", credential.Store, appId, e.Message);
            state.LastError = e is StoreAccessException ? e.Message : "Lo store non risponde.";
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SyncAppleAsync(AppleSession session, Guid tenantId, string appId, int pages, CancellationToken ct)
    {
        var (data, included) = await session.ListAsync(
            $"v1/apps/{appId}/customerReviews?sort=-createdDate&limit=200&include=response" +
            "&fields[customerReviews]=rating,title,body,reviewerNickname,createdDate,territory,response" +
            "&fields[customerReviewResponses]=responseBody,lastModifiedDate,state",
            ct, maxPages: pages);

        var responses = included.Where(i => i["type"]?.GetValue<string>() == "customerReviewResponses").ToDictionary(i => i.Id());
        var existing = await ExistingAsync(Store.AppStore, data.Select(d => d.Id()), ct);

        foreach (var node in data)
        {
            var review = existing.GetValueOrDefault(node.Id()) ?? Add(tenantId, Store.AppStore, appId, node.Id());
            review.Update(node.Attr<int>("rating"), node.Attr("title"), node.Attr("body") ?? "", node.Attr("reviewerNickname"),
                node.Attr("territory"), null, ParseDate(node.Attr("createdDate")));

            if (node.RelatedId("response") is { } responseId && responses.TryGetValue(responseId, out var response))
            {
                review.SetReply(response.Attr("responseBody"), ParseDate(response.Attr("lastModifiedDate")), responseId, response.Attr("state"));
            }
        }
    }

    private async Task SyncGoogleAsync(GoogleSession session, Guid tenantId, string packageName, CancellationToken ct)
    {
        var all = new List<JsonNode>();
        string? token = null;
        do
        {
            var page = await session.GetAsync($"{GooglePublisher.App(packageName)}/reviews?maxResults=100" +
                (token is null ? "" : "&token=" + Uri.EscapeDataString(token)), ct);
            all.AddRange(page?["reviews"]?.AsArray().OfType<JsonNode>() ?? []);
            token = page?["tokenPagination"]?["nextPageToken"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(token));

        var existing = await ExistingAsync(Store.GooglePlay, all.Select(r => r["reviewId"]!.GetValue<string>()), ct);

        foreach (var node in all)
        {
            var id = node["reviewId"]!.GetValue<string>();
            var comments = node["comments"]?.AsArray().OfType<JsonNode>().ToList() ?? [];
            var user = comments.Select(c => c["userComment"]).FirstOrDefault(c => c is not null);
            var developer = comments.Select(c => c["developerComment"]).FirstOrDefault(c => c is not null);
            if (user is null) continue;

            var review = existing.GetValueOrDefault(id) ?? Add(tenantId, Store.GooglePlay, packageName, id);
            review.Update(user["starRating"]?.GetValue<int>() ?? 0, null, user["text"]?.GetValue<string>() ?? "",
                node["authorName"]?.GetValue<string>(), user["reviewerLanguage"]?.GetValue<string>(),
                user["appVersionName"]?.GetValue<string>(), FromSeconds(user["lastModified"]));

            if (developer is not null) review.SetReply(developer["text"]?.GetValue<string>(), FromSeconds(developer["lastModified"]));
        }
    }

    /// <summary>Risponde a una recensione sullo store e aggiorna la copia locale.</summary>
    public async Task ReplyAsync(Review review, StoreCredential credential, string text, CancellationToken ct)
    {
        await secrets.UseAsync(credential, async secret =>
        {
            if (review.Store == Store.AppStore)
            {
                // Apple ammette una risposta per recensione: se c'è già si
                // cancella e se ne crea una nuova, che è il modo di modificarla.
                var session = apple.Open(credential, secret);
                if (review.ReplyExternalId is { } previous) await session.DeleteAsync("customerReviewResponses", previous, ct);

                var created = await session.CreateAsync("customerReviewResponses", new { responseBody = text },
                    new Dictionary<string, (string, string)> { ["review"] = ("customerReviews", review.ExternalId) }, ct);
                var data = created?["data"];
                review.SetReply(text, DateTime.UtcNow, data?.Id(), data?.Attr("state"));
            }
            else
            {
                var session = await google.OpenAsync(secret, ct);
                await session.SendJsonAsync(HttpMethod.Post,
                    $"{GooglePublisher.App(review.AppId)}/reviews/{Uri.EscapeDataString(review.ExternalId)}:reply", new { replyText = text }, ct);
                review.SetReply(text, DateTime.UtcNow);
            }
            return true;
        }, ct);

        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, Review>> ExistingAsync(Store store, IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.Set<Review>().Where(r => r.Store == store && list.Contains(r.ExternalId)).ToDictionaryAsync(r => r.ExternalId, ct);
    }

    private Review Add(Guid tenantId, Store store, string appId, string id)
    {
        var review = Review.Create(tenantId, store, appId, id);
        db.Add(review);
        return review;
    }

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, out var d) ? d.ToUniversalTime() : DateTime.UtcNow;

    private static DateTime FromSeconds(JsonNode? timestamp) =>
        long.TryParse(timestamp?["seconds"]?.ToString(), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.UtcNow;
}
