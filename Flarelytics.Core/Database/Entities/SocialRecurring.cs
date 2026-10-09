using Flarelytics.Core.Social;

namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Un post che esce a intervalli regolari (ogni giorno, certi giorni della
/// settimana, una volta al mese): il contenuto, gli account e la regola.
/// </summary>
/// <remarks>
/// <para>Non si preparano le uscite in anticipo: quando arriva
/// <see cref="NextOccurrenceUtc"/> il worker crea un <see cref="SocialPost"/>
/// normale (con <see cref="SocialPost.RecurringPostId"/>) e lo pubblica nello
/// stesso giro. Così una modifica alla serie vale per tutte le uscite future,
/// senza post già pronti da tenere allineati; il calendario le uscite future
/// le calcola dalla regola.</para>
///
/// <para>Gli account sono un elenco di id e non dei target: un target è un
/// esito, e la serie non ne ha. Un account scollegato si toglie anche da qui.</para>
/// </remarks>
public class SocialRecurringPost : BaseEntity, ITenantOwned
{
    private readonly List<SocialMedia> _media = [];

    public Guid TenantId { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string Text { get; private set; } = "";

    /// <summary>Le scelte per le singole reti, come per un post (vedi <see cref="Social.PostOptions"/>).</summary>
    public string? OptionsJson { get; private set; }

    public PostOptions Options => PostOptions.FromJson(OptionsJson);

    public List<Guid> AccountIds { get; private set; } = [];

    public RecurrenceFrequency Frequency { get; private set; }

    /// <summary>Ogni quanti giorni, settimane o mesi (1 = ogni volta).</summary>
    public int Interval { get; private set; } = 1;

    /// <summary>Solo settimanale: i giorni, un bit per giorno (1 &lt;&lt; <see cref="DayOfWeek"/>, domenica = 1).</summary>
    public int DaysOfWeek { get; private set; }

    /// <summary>L'ora locale dell'uscita, nel fuso <see cref="TimeZone"/>: "alle 9" resta alle 9 anche con l'ora legale.</summary>
    public TimeOnly TimeOfDay { get; private set; }

    /// <summary>Fuso IANA (Europe/Rome), quello del browser di chi l'ha creata.</summary>
    public string TimeZone { get; private set; } = "UTC";

    /// <summary>Il primo giorno possibile. Per il mensile dà anche il giorno del mese.</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>L'ultimo giorno possibile (compreso); null = senza fine.</summary>
    public DateOnly? EndDate { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>La prossima uscita, già calcolata: il worker cerca solo questa colonna. Null = la serie è finita.</summary>
    public DateTime? NextOccurrenceUtc { get; private set; }

    public DateTime? LastOccurrenceUtc { get; private set; }

    /// <summary>Quante uscite ha avuto (quelle saltate perché il server era spento non contano).</summary>
    public int OccurrenceCount { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public IReadOnlyList<SocialMedia> Media => _media;

    public RecurrenceRule Rule => new(Frequency, Interval, DaysOfWeek, TimeOfDay, TimeZone, StartDate, EndDate);

    private SocialRecurringPost() { }

    public static SocialRecurringPost Create(Guid tenantId, Guid createdBy) => new() { TenantId = tenantId, CreatedByUserId = createdBy };

    public void Update(string text, Guid? projectId, IEnumerable<Guid> accountIds, PostOptions options)
    {
        Text = text.Trim();
        ProjectId = projectId;
        AccountIds = accountIds.Distinct().ToList();
        OptionsJson = options.ToJson();
    }

    /// <summary>Nuova regola: la prossima uscita si ricalcola da adesso, quelle già passate non si recuperano.</summary>
    public void SetRule(RecurrenceRule rule, DateTime nowUtc)
    {
        (Frequency, Interval, DaysOfWeek, TimeOfDay, TimeZone, StartDate, EndDate) =
            (rule.Frequency, rule.Interval, rule.DaysOfWeek, rule.TimeOfDay, rule.TimeZone, rule.StartDate, rule.EndDate);
        NextOccurrenceUtc = Rule.NextAfter(nowUtc);
    }

    /// <summary>
    /// In pausa le uscite non partono. Riprendendo si riparte dalla prossima
    /// dopo adesso: quelle della pausa non escono tutte insieme.
    /// </summary>
    public void SetPaused(bool paused, DateTime nowUtc)
    {
        if (IsPaused && !paused) NextOccurrenceUtc = Rule.NextAfter(nowUtc);
        IsPaused = paused;
    }

    /// <summary>L'uscita di <paramref name="atUtc"/> è stata creata (o saltata): si passa alla prossima dopo adesso.</summary>
    public void Advance(DateTime atUtc, bool published, DateTime nowUtc)
    {
        if (published)
        {
            LastOccurrenceUtc = atUtc;
            OccurrenceCount++;
        }
        NextOccurrenceUtc = Rule.NextAfter(nowUtc > atUtc ? nowUtc : atUtc);
    }

    public void RemoveAccount(Guid accountId) => AccountIds = AccountIds.Where(id => id != accountId).ToList();

    public void AddMedia(SocialMedia media) => _media.Add(media);
    public void RemoveMedia(SocialMedia media) => _media.Remove(media);
}

public enum RecurrenceFrequency
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2
}
