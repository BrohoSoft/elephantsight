namespace Flarelytics.Core.Database.Entities;

public enum SocialNetwork
{
    Bluesky = 0,
    Mastodon = 1,
    Instagram = 2,
    FacebookPage = 3,
    TikTok = 4,
    Threads = 5
}

public enum SocialAccountStatus
{
    Connected = 0,

    /// <summary>La rete non accetta più il token o la password: va ricollegato.</summary>
    NeedsReconnect = 1
}

/// <summary>
/// Un account social dell'organizzazione su cui si può pubblicare: un profilo
/// Bluesky, un account Mastodon, un account Instagram professionale, una
/// Pagina Facebook.
/// </summary>
/// <remarks>
/// Il token (o la password per app di Bluesky) sta cifrato in colonna con
/// <see cref="Secrets.FieldProtector"/>, legato a tenant e account: è piccolo,
/// e un file per account non darebbe niente in più. Nessuna rotta lo restituisce.
/// </remarks>
public class SocialAccount : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public SocialNetwork Network { get; private set; }

    /// <summary>Bluesky: il DID. Mastodon: id@istanza. Meta: l'id dell'account Instagram o della Pagina.</summary>
    public string ExternalId { get; private set; } = null!;

    public string Name { get; private set; } = null!;
    public string? Handle { get; private set; }

    /// <summary>Bluesky: il PDS. Mastodon: l'istanza. Meta: niente, l'host è sempre lo stesso.</summary>
    public string? ServerUrl { get; private set; }

    /// <summary>Mastodon: il limite di caratteri dell'istanza, che non è uguale per tutte.</summary>
    public int? MaxCharacters { get; private set; }

    public string ProtectedSecret { get; private set; } = null!;

    /// <summary>Instagram Login e Threads: il token dura 60 giorni e il worker lo rinnova prima (TikTok: 24 ore). Null se non scade.</summary>
    public DateTime? TokenExpiresAtUtc { get; private set; }

    public SocialAccountStatus Status { get; private set; }
    public string? StatusMessage { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    /// <summary>
    /// I progetti in cui l'account si può usare. Un account è
    /// dell'organizzazione e si condivide: lo stesso Threads può servire a
    /// promuovere un'app e a pubblicare per sé, con post diversi in due
    /// progetti. Un post di un progetto usa solo gli account collegati a quel progetto.
    /// </summary>
    public List<Guid> ProjectIds { get; private set; } = [];

    /// <summary>L'ultima lettura dei post pubblicati fuori da ElephantSight (vedi <see cref="Social.SocialImporter"/>).</summary>
    public DateTime? LastImportAtUtc { get; private set; }

    private SocialAccount() { }

    public static SocialAccount Create(Guid tenantId, SocialNetwork network, string externalId, string name, string? handle,
        string? serverUrl, Guid createdBy) => new()
    {
        TenantId = tenantId, Network = network, ExternalId = externalId, Name = name, Handle = handle, ServerUrl = serverUrl,
        CreatedByUserId = createdBy
    };

    /// <summary>Il contesto con cui si cifra il segreto: un valore copiato su un altro account non si decifra.</summary>
    public string SecretContext => $"social|{TenantId:N}|{Id:N}";

    /// <summary>Un nuovo collegamento dello stesso account: nuovo segreto, nomi aggiornati, di nuovo funzionante.</summary>
    /// <param name="serverUrl">Instagram: con quale login è collegato (vedi <see cref="Social.InstagramLoginClient.Host"/>); per le altre reti non cambia.</param>
    public void Reconnect(string protectedSecret, string name, string? handle, int? maxCharacters, DateTime? tokenExpiresAtUtc = null, string? serverUrl = null)
    {
        ProtectedSecret = protectedSecret;
        TokenExpiresAtUtc = tokenExpiresAtUtc;
        if (Network == SocialNetwork.Instagram) ServerUrl = serverUrl;
        Name = name;
        Handle = handle;
        MaxCharacters = maxCharacters;
        Status = SocialAccountStatus.Connected;
        StatusMessage = null;
    }

    public void MarkImported(DateTime nowUtc) => LastImportAtUtc = nowUtc;

    public void SetProjects(IEnumerable<Guid> projectIds) => ProjectIds = projectIds.Distinct().ToList();

    public void ForgetProject(Guid projectId) => ProjectIds = ProjectIds.Where(id => id != projectId).ToList();

    public bool IsInProject(Guid projectId) => ProjectIds.Contains(projectId);

    /// <summary>Il token rinnovato dal worker.</summary>
    public void RenewToken(string protectedSecret, DateTime expiresAtUtc)
    {
        ProtectedSecret = protectedSecret;
        TokenExpiresAtUtc = expiresAtUtc;
    }

    public void MarkBroken(string message)
    {
        Status = SocialAccountStatus.NeedsReconnect;
        StatusMessage = message;
    }
}

/// <summary>
/// Un post del calendario: testo, immagini e, nei <see cref="Targets"/>, gli
/// account su cui va pubblicato.
/// </summary>
/// <remarks>
/// La data c'è sempre, anche per le bozze: una bozza sta sul calendario nel
/// giorno in cui si pensa di pubblicarla, ma il worker non la tocca.
/// </remarks>
public class SocialPost : BaseEntity, ITenantOwned
{
    private readonly List<SocialPostTarget> _targets = [];
    private readonly List<SocialMedia> _media = [];

    public Guid TenantId { get; private set; }

    /// <summary>Facoltativo: il progetto (l'app) di cui parla il post, per filtrare il calendario.</summary>
    public Guid? ProjectId { get; private set; }

    public string Text { get; private set; } = null!;
    public DateTime ScheduledAtUtc { get; private set; }
    public bool IsDraft { get; private set; }

    /// <summary>
    /// Pubblicato fuori da ElephantSight (Business Suite, l'app, il sito) e
    /// copiato qui per vederlo sul calendario: si legge e basta.
    /// </summary>
    public bool IsImported { get; private set; }

    /// <summary>
    /// Arrivato da un programma esterno con una chiave API e in attesa nella
    /// coda "Da programmare": non sta sul calendario finché qualcuno non sceglie
    /// account e ora. Il worker non lo tocca (è anche una bozza).
    /// </summary>
    public bool IsInbox { get; private set; }

    /// <summary>La data proposta da chi l'ha mandato, se l'ha proposta: si usa quando lo si programma.</summary>
    public DateTime? SuggestedAtUtc { get; private set; }

    /// <summary>
    /// Il post ricorrente da cui è nato (null se cancellato, o se è un post
    /// singolo): il worker lo crea quando arriva l'ora di un'uscita.
    /// </summary>
    public Guid? RecurringPostId { get; private set; }

    /// <summary>La chiave con cui è arrivato (null se cancellata, o se creato dal pannello).</summary>
    public Guid? ApiKeyId { get; private set; }

    /// <summary>Un riferimento scelto da chi manda il post (l'id nel suo CMS): mandarlo due volte non crea un doppione.</summary>
    public string? ExternalRef { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>Le scelte per le singole reti (griglia di Instagram, privacy di TikTok…), in JSON: vedi <see cref="Social.PostOptions"/>.</summary>
    public string? OptionsJson { get; private set; }

    public Social.PostOptions Options => Social.PostOptions.FromJson(OptionsJson);

    public void SetOptions(Social.PostOptions options) => OptionsJson = options.ToJson();

    public IReadOnlyList<SocialPostTarget> Targets => _targets;
    public IReadOnlyList<SocialMedia> Media => _media;

    private SocialPost() { }

    public static SocialPost Create(Guid tenantId, Guid createdBy) => new() { TenantId = tenantId, CreatedByUserId = createdBy, Text = "" };

    public static SocialPost FromApi(Guid tenantId, Guid apiKeyId, Guid createdBy, string text, DateTime? suggestedAtUtc,
        string? externalRef, Guid? projectId, DateTime nowUtc) => new()
    {
        TenantId = tenantId, ApiKeyId = apiKeyId, CreatedByUserId = createdBy, Text = text.Trim(), ProjectId = projectId,
        SuggestedAtUtc = suggestedAtUtc is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null,
        // La data c'è sempre (vedi sopra): finché è in coda vale quella proposta, o il momento dell'arrivo.
        ScheduledAtUtc = suggestedAtUtc is { } at ? DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc) : nowUtc,
        IsDraft = true, IsInbox = true, ExternalRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim()
    };

    /// <summary>Un'uscita di un post ricorrente: programmata per adesso, con il testo e le scelte della serie.</summary>
    public static SocialPost FromRecurring(SocialRecurringPost recurring, DateTime atUtc) => new()
    {
        TenantId = recurring.TenantId, CreatedByUserId = recurring.CreatedByUserId, Text = recurring.Text, ProjectId = recurring.ProjectId,
        ScheduledAtUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc), RecurringPostId = recurring.Id, OptionsJson = recurring.OptionsJson
    };

    /// <summary>Programmato: esce dalla coda ed entra nel calendario come un post qualsiasi.</summary>
    public void LeaveInbox() => IsInbox = false;

    /// <summary>
    /// In coda "Da programmare", scritto a mano dal pannello: come quelli che
    /// arrivano con una chiave API, è una bozza con una data proposta facoltativa.
    /// </summary>
    public void PutInInbox(DateTime? suggestedAtUtc, DateTime nowUtc)
    {
        IsInbox = true;
        IsDraft = true;
        SuggestedAtUtc = suggestedAtUtc is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null;
        // La data c'è sempre (vedi sopra): finché è in coda vale quella proposta, o adesso.
        ScheduledAtUtc = SuggestedAtUtc ?? DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
    }

    /// <param name="projectId">Il progetto dell'account, se ne ha uno solo; con più progetti non si sa a quale appartenga e resta dell'organizzazione.</param>
    public static SocialPost Imported(Guid tenantId, string text, DateTime publishedAtUtc, Guid createdBy, Guid? projectId = null) => new()
    {
        TenantId = tenantId, Text = text.Trim(), ScheduledAtUtc = DateTime.SpecifyKind(publishedAtUtc, DateTimeKind.Utc),
        IsImported = true, CreatedByUserId = createdBy, ProjectId = projectId
    };

    public void Update(string text, DateTime scheduledAtUtc, bool isDraft, Guid? projectId)
    {
        Text = text.Trim();
        ScheduledAtUtc = DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc);
        IsDraft = isDraft;
        ProjectId = projectId;
    }

    public void AddTarget(SocialPostTarget target) => _targets.Add(target);
    public void RemoveTarget(SocialPostTarget target) => _targets.Remove(target);

    public void AddMedia(SocialMedia media) => _media.Add(media);
    public void RemoveMedia(SocialMedia media) => _media.Remove(media);

    /// <summary>
    /// Si modifica solo finché nessuna rete l'ha pubblicato né lo sta
    /// pubblicando: dopo, il testo sul calendario non sarebbe più quello uscito.
    /// </summary>
    public bool IsEditable => !IsImported && _targets.All(t => t.Status is SocialTargetStatus.Pending or SocialTargetStatus.Failed);
}

public enum SocialTargetStatus
{
    Pending = 0,
    Publishing = 1,
    Published = 2,
    Failed = 3
}

/// <summary>Un post su un account: ognuno ha il suo esito, il suo link e il suo errore.</summary>
public class SocialPostTarget : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PostId { get; private set; }

    /// <summary>Null se l'account è stato scollegato: il post pubblicato resta nello storico.</summary>
    public Guid? AccountId { get; private set; }

    // Copie dei dati dell'account, per mostrare lo storico anche senza.
    public SocialNetwork Network { get; private set; }
    public string AccountName { get; private set; } = null!;

    /// <summary>Un testo diverso solo per questo account (più corto per Bluesky, con gli hashtag per Instagram…).</summary>
    public string? TextOverride { get; private set; }

    public SocialTargetStatus Status { get; private set; }
    public string? ExternalId { get; private set; }
    public string? ExternalUrl { get; private set; }
    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }

    /// <summary>
    /// Instagram: il container già creato. Se il processo si ferma fra la
    /// creazione e la pubblicazione, al giro dopo si riparte da lì invece di
    /// crearne un secondo.
    /// </summary>
    public string? ProgressState { get; private set; }

    /// <summary>Da quando la rete sta elaborando (un Reel, un video TikTok): oltre un tempo massimo si rinuncia.</summary>
    public DateTime? ProgressStartedAtUtc { get; private set; }

    private SocialPostTarget() { }

    public static SocialPostTarget For(SocialPost post, SocialAccount account) => new()
    {
        TenantId = post.TenantId, PostId = post.Id, AccountId = account.Id, Network = account.Network, AccountName = account.Name
    };

    public void SetTextOverride(string? text) => TextOverride = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public void Start()
    {
        Status = SocialTargetStatus.Publishing;
        Attempts++;
        Error = null;
    }

    public void SetProgress(string? state, DateTime? nowUtc = null)
    {
        ProgressState = state;
        ProgressStartedAtUtc = state is null ? null : ProgressStartedAtUtc ?? nowUtc ?? DateTime.UtcNow;
    }

    /// <summary>
    /// La rete sta ancora elaborando il video: si ricontrolla più tardi. Non
    /// è un errore e non consuma un tentativo.
    /// </summary>
    public void StillProcessing(DateTime nextCheckAtUtc)
    {
        Status = SocialTargetStatus.Pending;
        NextAttemptAtUtc = nextCheckAtUtc;
        Attempts = Math.Max(0, Attempts - 1);
        Error = null;
    }

    public void Published(string? externalId, string? url, DateTime nowUtc)
    {
        Status = SocialTargetStatus.Published;
        ExternalId = externalId;
        ExternalUrl = url;
        PublishedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        Error = null;
    }

    /// <summary>Un errore passeggero: si riprova più tardi, finché restano tentativi.</summary>
    public void RetryLater(string error, DateTime atUtc)
    {
        Status = SocialTargetStatus.Pending;
        Error = error;
        NextAttemptAtUtc = atUtc;
    }

    public void Fail(string error)
    {
        Status = SocialTargetStatus.Failed;
        Error = error;
        NextAttemptAtUtc = null;
    }

    /// <summary>"Riprova" dal pannello: da capo, con tutti i tentativi.</summary>
    public void Requeue()
    {
        Status = SocialTargetStatus.Pending;
        Attempts = 0;
        NextAttemptAtUtc = null;
        Error = null;
    }

    /// <summary>Riavvio a metà pubblicazione: si riprova solo dove ripetere non crea un doppione.</summary>
    public void Interrupted(bool safeToRetry)
    {
        if (safeToRetry) Status = SocialTargetStatus.Pending;
        else Fail("Pubblicazione interrotta da un riavvio: controlla sul social se il post è uscito, poi usa Riprova se manca.");
    }
}

public enum MediaKind
{
    Image = 0,
    Video = 1
}

/// <summary>
/// Un'immagine o un video di un post, su disco.
/// </summary>
/// <remarks>
/// <para>Le immagini sono sempre JPEG: le converte il pannello prima di
/// caricarle (Instagram vuole solo JPEG, Bluesky meno di 1 MB). I video
/// arrivano come sono (MP4 o MOV): il server ne legge durata, dimensioni e se
/// l'indice sta in testa (<see cref="Social.Mp4Info"/>), senza ricodificarli.</para>
///
/// <para>Si carica prima del post (<see cref="PostId"/> null) e ci si attacca
/// al salvataggio. Quelli mai attaccati li cancella il worker dopo un giorno.</para>
/// </remarks>
public class SocialMedia : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid? PostId { get; private set; }

    /// <summary>
    /// Il post ricorrente a cui appartiene (al posto di <see cref="PostId"/>):
    /// a ogni uscita se ne fa una copia, attaccata al post creato.
    /// </summary>
    public Guid? RecurringPostId { get; private set; }

    public int Position { get; private set; }
    public MediaKind Kind { get; private set; }

    /// <summary>image/jpeg, video/mp4 o video/quicktime.</summary>
    public string ContentType { get; private set; } = "image/jpeg";

    public string FileName { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Solo video.</summary>
    public int? DurationMs { get; private set; }

    /// <summary>Solo video: l'indice (moov) sta prima dei dati, come vuole Instagram per i Reel.</summary>
    public bool FastStart { get; private set; }

    public string? AltText { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    private SocialMedia() { }

    public static SocialMedia Create(Guid tenantId, string fileName, long size, int width, int height, Guid createdBy) => new()
    {
        TenantId = tenantId, FileName = fileName, SizeBytes = size, Width = width, Height = height, CreatedByUserId = createdBy,
        Kind = MediaKind.Image, ContentType = "image/jpeg"
    };

    public static SocialMedia CreateVideo(Guid tenantId, string fileName, long size, Social.VideoInfo info, Guid createdBy) => new()
    {
        TenantId = tenantId, FileName = fileName, SizeBytes = size, Width = info.Width, Height = info.Height, CreatedByUserId = createdBy,
        Kind = MediaKind.Video, ContentType = info.ContentType, DurationMs = info.DurationMs, FastStart = info.FastStart
    };

    /// <summary>
    /// Un'altra riga per lo stesso contenuto (il file va copiato a parte): ogni
    /// uscita di un post ricorrente ha i suoi file, che restano con il post
    /// anche se la serie cambia immagini o viene cancellata. Con
    /// <paramref name="postId"/> null la copia resta libera, come appena
    /// caricata (per duplicare un post ricorrente nell'editor).
    /// </summary>
    public SocialMedia CopyFor(Guid? postId, int position) => new()
    {
        TenantId = TenantId, FileName = FileName, SizeBytes = SizeBytes, Width = Width, Height = Height, CreatedByUserId = CreatedByUserId,
        Kind = Kind, ContentType = ContentType, DurationMs = DurationMs, FastStart = FastStart, PostId = postId, Position = position, AltText = AltText
    };

    /// <summary>L'estensione del file su disco e nell'indirizzo firmato.</summary>
    public string Extension => ContentType switch
    {
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        _ => ".jpg"
    };

    public void AttachToRecurring(Guid recurringPostId, int position, string? altText)
    {
        RecurringPostId = recurringPostId;
        Position = position;
        AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
    }

    public void AttachTo(Guid postId, int position, string? altText)
    {
        PostId = postId;
        Position = position;
        AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
    }
}
