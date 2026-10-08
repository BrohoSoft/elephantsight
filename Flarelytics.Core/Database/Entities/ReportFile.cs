namespace Flarelytics.Core.Database.Entities;

public enum ReportKind
{
    /// <summary>Apple, Sales and Trends, SALES / SUMMARY / DAILY: vendite e download di tutte le app dell'account.</summary>
    AppleSalesDaily = 0,

    /// <summary>
    /// Google Play, report in blocco <c>stats/installs/…_country.csv</c>: un
    /// file per app e per mese, che Google riscrive ogni giorno finché il mese
    /// non è chiuso.
    /// </summary>
    GooglePlayInstallsMonthly = 1
}

public enum ReportFileStatus
{
    /// <summary>Scaricato e salvato su disco.</summary>
    Stored = 0,

    /// <summary>
    /// Lo store ha risposto che per quel giorno non c'è un report. Per un
    /// giorno vecchio vuol dire nessuna vendita né download; per uno recente
    /// può voler dire "non ancora pronto", e il worker riprova.
    /// </summary>
    Empty = 1
}

/// <summary>
/// Un report scaricato da uno store, così come è arrivato.
/// </summary>
/// <remarks>
/// <para>Il file su disco è la fonte di verità: le metriche si ricalcolano da
/// qui ogni volta che cambia il modo di leggerle (<see cref="ParserVersion"/>),
/// senza riscaricare niente. Conta perché gli store non tengono lo storico per
/// sempre: Apple, per esempio, i report giornalieri li tiene circa un anno.</para>
///
/// <para>Appartiene alla credenziale e non all'app: un report di Apple
/// contiene tutte le app dell'account sviluppatore.</para>
/// </remarks>
public class ReportFile : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CredentialId { get; private set; }
    public ReportKind Kind { get; private set; }
    public DateOnly ReportDate { get; private set; }

    /// <summary>
    /// Cosa distingue due file dello stesso tipo e giorno: per Google il
    /// package name, perché ogni app ha il suo file mensile. Vuoto per Apple,
    /// dove un report contiene tutte le app.
    /// </summary>
    public string Scope { get; private set; } = "";

    /// <summary>
    /// L'impronta del contenuto secondo lo store (per Google l'MD5 di Cloud
    /// Storage). Un file mensile riscritto con gli stessi dati non si
    /// riscarica né si rielabora.
    /// </summary>
    public string? ContentHash { get; private set; }

    public ReportFileStatus Status { get; private set; }

    /// <summary>Percorso relativo alla cartella dei report. Null se il report era vuoto.</summary>
    public string? RelativePath { get; private set; }
    public long SizeBytes { get; private set; }
    public DateTime FetchedAtUtc { get; private set; }

    /// <summary>Quando le metriche sono state ricavate da questo file. Null finché non succede.</summary>
    public DateTime? ProcessedAtUtc { get; private set; }

    /// <summary>La versione del parser che l'ha letto. Se il parser cambia, il file si rilegge.</summary>
    public int ParserVersion { get; private set; }

    private ReportFile() { }

    public static ReportFile Create(Guid tenantId, Guid credentialId, ReportKind kind, DateOnly date, string scope = "") => new()
    {
        TenantId = tenantId,
        CredentialId = credentialId,
        Kind = kind,
        ReportDate = date,
        Scope = scope
    };

    public void MarkStored(string relativePath, long sizeBytes, DateTime nowUtc, string? contentHash = null)
    {
        Status = ReportFileStatus.Stored;
        RelativePath = relativePath;
        SizeBytes = sizeBytes;
        FetchedAtUtc = nowUtc;
        ContentHash = contentHash;
        ProcessedAtUtc = null;
    }

    public void MarkEmpty(DateTime nowUtc)
    {
        Status = ReportFileStatus.Empty;
        RelativePath = null;
        SizeBytes = 0;
        FetchedAtUtc = nowUtc;
        ProcessedAtUtc = nowUtc;
    }

    public void MarkProcessed(int parserVersion, DateTime nowUtc)
    {
        ParserVersion = parserVersion;
        ProcessedAtUtc = nowUtc;
    }
}
