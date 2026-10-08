namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Una recensione di un'app, da App Store o Google Play, con l'eventuale
/// risposta dello sviluppatore.
/// </summary>
/// <remarks>
/// Copiata da noi e non letta ogni volta dallo store: Google via API dà solo le
/// recensioni dell'ultima settimana, quindi senza copia lo storico si perde.
/// Legata all'app (store + id), come le metriche, non al progetto.
/// </remarks>
public class Review : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Store Store { get; private set; }
    public string AppId { get; private set; } = null!;

    /// <summary>L'id della recensione sullo store: customerReviews id per Apple, reviewId per Google.</summary>
    public string ExternalId { get; private set; } = null!;

    public int Rating { get; private set; }
    public string? Title { get; private set; }
    public string Body { get; private set; } = "";
    public string? Author { get; private set; }

    /// <summary>Apple: il paese (territorio, ISO a tre lettere). Google: la lingua del recensore.</summary>
    public string? Locale { get; private set; }
    public string? AppVersion { get; private set; }

    /// <summary>Quando è stata scritta, o modificata l'ultima volta (Google tiene solo quello).</summary>
    public DateTime WrittenAtUtc { get; private set; }

    public string? ReplyText { get; private set; }
    public DateTime? RepliedAtUtc { get; private set; }

    /// <summary>Apple: l'id della risposta, per poterla sostituire; e il suo stato (PENDING_PUBLISH finché Apple non la pubblica).</summary>
    public string? ReplyExternalId { get; private set; }
    public string? ReplyState { get; private set; }

    private Review() { }

    public static Review Create(Guid tenantId, Store store, string appId, string externalId) =>
        new() { TenantId = tenantId, Store = store, AppId = appId, ExternalId = externalId };

    public void Update(int rating, string? title, string body, string? author, string? locale, string? appVersion, DateTime writtenAtUtc)
    {
        Rating = Math.Clamp(rating, 1, 5);
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Body = body.Trim();
        Author = author;
        Locale = locale;
        AppVersion = appVersion;
        WrittenAtUtc = writtenAtUtc;
    }

    public void SetReply(string? text, DateTime? atUtc, string? externalId = null, string? state = null)
    {
        ReplyText = string.IsNullOrWhiteSpace(text) ? null : text;
        RepliedAtUtc = ReplyText is null ? null : atUtc;
        ReplyExternalId = externalId;
        ReplyState = state;
    }
}

/// <summary>A che punto è la sincronizzazione delle recensioni di un'app, e l'ultimo errore.</summary>
public class ReviewSyncState : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Store Store { get; set; }
    public string AppId { get; set; } = null!;
    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastError { get; set; }
}
