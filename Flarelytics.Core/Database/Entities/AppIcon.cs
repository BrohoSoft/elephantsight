namespace Flarelytics.Core.Database.Entities;

public enum AppIconStatus
{
    Stored = 0,

    /// <summary>Lo store non ha una pagina pubblica per l'app: non ancora pubblicata, o ritirata.</summary>
    NotFound = 1,

    /// <summary>Errore di rete o risposta inattesa: si riprova più tardi.</summary>
    Failed = 2
}

/// <summary>
/// L'icona di un'app, presa dalla sua pagina pubblica sullo store e salvata da
/// noi.
/// </summary>
/// <remarks>
/// <para><b>Non è del tenant.</b> L'icona è pubblica e uguale per chiunque
/// segua quell'app: si scarica una volta sola anche se due organizzazioni
/// collegano la stessa app.</para>
///
/// <para><b>L'indirizzo è <see cref="BaseEntity.Id"/>, non l'id dell'app.</b>
/// L'immagine si serve senza autenticazione (un <c>&lt;img&gt;</c> non manda
/// il bearer token), e un indirizzo con l'Apple ID permetterebbe a chiunque di
/// scoprire, provando, quali app sono seguite su Flarelytics. Un Guid casuale
/// non si indovina, e lo conosce solo chi vede il progetto.</para>
/// </remarks>
public class AppIcon : BaseEntity
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(1);

    public Store Store { get; private set; }
    public string AppId { get; private set; } = null!;
    public AppIconStatus Status { get; private set; }
    public string? ContentType { get; private set; }
    public string? RelativePath { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }

    private AppIcon() { }

    public static AppIcon Create(Store store, string appId) => new() { Store = store, AppId = appId, Status = AppIconStatus.Failed };

    public void MarkStored(string relativePath, string contentType, DateTime nowUtc)
    {
        Status = AppIconStatus.Stored;
        RelativePath = relativePath;
        ContentType = contentType;
        NextAttemptAtUtc = nowUtc.Add(RefreshAfter);
    }

    /// <summary>Un tentativo non riuscito non cancella l'icona che c'era: la vecchia resta finché non ne arriva una nuova.</summary>
    public void MarkUnavailable(AppIconStatus status, DateTime nowUtc)
    {
        if (Status != AppIconStatus.Stored) Status = status;
        NextAttemptAtUtc = nowUtc.Add(RetryAfter);
    }
}
