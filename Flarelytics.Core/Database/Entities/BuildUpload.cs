namespace Flarelytics.Core.Database.Entities;

public enum BuildUploadStatus
{
    /// <summary>Il file è arrivato al server e aspetta il suo turno.</summary>
    Queued = 0,

    /// <summary>Lo si sta mandando allo store.</summary>
    Uploading = 1,

    /// <summary>Apple l'ha ricevuto e lo sta elaborando: può durare decine di minuti.</summary>
    Processing = 2,

    Completed = 3,
    Failed = 4
}

/// <summary>
/// Una build caricata dal pannello verso uno store: un .aab per Google Play,
/// un .ipa per App Store Connect.
/// </summary>
/// <remarks>
/// Il caricamento allo store lo fa il worker, non la richiesta HTTP: un file da
/// centinaia di MB può metterci minuti, e la pagina non deve restare appesa.
/// Il file sta su disco solo finché serve, poi si cancella.
/// </remarks>
public class BuildUpload : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Store Store { get; private set; }
    public string AppId { get; private set; } = null!;
    public Guid CredentialId { get; private set; }
    public string FileName { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public string? StoragePath { get; private set; }

    /// <summary>Il numero visibile (Apple: CFBundleShortVersionString; Google: il nome della release, facoltativo).</summary>
    public string? Version { get; private set; }

    /// <summary>Apple: CFBundleVersion, letto dall'.ipa. Google: il versionCode, noto dopo il caricamento.</summary>
    public string? BuildNumber { get; private set; }

    // Solo Google: dove e come rilasciarla.
    public string? Track { get; private set; }
    public string? ReleaseStatus { get; private set; }
    public double? RolloutPercent { get; private set; }
    public string? ReleaseNotesLanguage { get; private set; }
    public string? ReleaseNotes { get; private set; }

    /// <summary>Apple: l'id del buildUpload, per seguirne l'elaborazione.</summary>
    public string? ExternalId { get; private set; }

    public BuildUploadStatus Status { get; private set; }
    public string? Message { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }

    private BuildUpload() { }

    public static BuildUpload ForApple(Guid tenantId, Guid projectId, ProjectApp app, string fileName, long size, string path,
        string version, string buildNumber, Guid createdBy) => new()
    {
        TenantId = tenantId, ProjectId = projectId, Store = Store.AppStore, AppId = app.ExternalAppId, CredentialId = app.CredentialId,
        FileName = fileName, SizeBytes = size, StoragePath = path, Version = version, BuildNumber = buildNumber, CreatedByUserId = createdBy
    };

    public static BuildUpload ForGoogle(Guid tenantId, Guid projectId, ProjectApp app, string fileName, long size, string path,
        string track, string releaseStatus, double? rolloutPercent, string? releaseName, string? notesLanguage, string? notes, Guid createdBy) => new()
    {
        TenantId = tenantId, ProjectId = projectId, Store = Store.GooglePlay, AppId = app.ExternalAppId, CredentialId = app.CredentialId,
        FileName = fileName, SizeBytes = size, StoragePath = path, Version = releaseName, Track = track, ReleaseStatus = releaseStatus,
        RolloutPercent = rolloutPercent, ReleaseNotesLanguage = notesLanguage, ReleaseNotes = notes, CreatedByUserId = createdBy
    };

    public void Start() { Status = BuildUploadStatus.Uploading; Message = null; }

    public void Processing(string externalId) { Status = BuildUploadStatus.Processing; ExternalId = externalId; }

    public void Complete(string? buildNumber, string? message, DateTime nowUtc)
    {
        Status = BuildUploadStatus.Completed;
        BuildNumber = buildNumber ?? BuildNumber;
        Message = message;
        FinishedAtUtc = nowUtc;
    }

    public void Fail(string message, DateTime nowUtc)
    {
        Status = BuildUploadStatus.Failed;
        Message = message;
        FinishedAtUtc = nowUtc;
    }

    /// <summary>Il file locale non serve più: il worker l'ha cancellato.</summary>
    public void ForgetFile() => StoragePath = null;
}
