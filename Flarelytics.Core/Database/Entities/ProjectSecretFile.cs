namespace Flarelytics.Core.Database.Entities;

public enum SecretPlatform
{
    Android = 0,
    Ios = 1,
    Common = 2
}

/// <summary>Che cosa è il file: serve al pannello per raggrupparlo e suggerire cosa manca.</summary>
public enum SecretKind
{
    /// <summary>Il keystore di firma Android (.jks, .keystore).</summary>
    AndroidKeystore = 0,

    /// <summary>key.properties, o qualunque file con password e alias del keystore.</summary>
    KeyProperties = 1,

    /// <summary>google-services.json di Firebase.</summary>
    GoogleServicesJson = 2,

    /// <summary>Il certificato di distribuzione iOS con la sua chiave (.p12).</summary>
    IosCertificate = 3,

    /// <summary>Profilo di provisioning (.mobileprovision).</summary>
    ProvisioningProfile = 4,

    /// <summary>GoogleService-Info.plist di Firebase.</summary>
    GoogleServiceInfoPlist = 5,

    /// <summary>Variabili d'ambiente o altri segreti in testo (.env, chiavi API).</summary>
    Environment = 6,

    Other = 7
}

/// <summary>
/// Un file segreto di un progetto: keystore, certificati, profili, config.
/// Il contenuto sta cifrato su disco come le chiavi degli store
/// (<see cref="Secrets.SecretVault"/>); qui solo i metadati.
/// </summary>
/// <remarks>
/// A differenza delle chiavi degli store, questi file si possono riscaricare:
/// servono per firmare le build fuori da qui. Lo scaricamento chiede di nuovo
/// password e codice della 2FA, e resta registrato chi l'ha fatto e quando.
/// </remarks>
public class ProjectSecretFile : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ProjectId { get; private set; }
    public SecretPlatform Platform { get; private set; }
    public SecretKind Kind { get; private set; }
    public string Name { get; private set; } = null!;
    public string FileName { get; private set; } = null!;
    public long SizeBytes { get; private set; }

    /// <summary>SHA-256 del contenuto: per riconoscere a colpo d'occhio se due file sono lo stesso keystore.</summary>
    public string Sha256 { get; private set; } = null!;
    public string? Notes { get; private set; }
    public string KeyVersion { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public DateTime? LastDownloadedAtUtc { get; private set; }
    public Guid? LastDownloadedByUserId { get; private set; }

    private ProjectSecretFile() { }

    public static ProjectSecretFile Create(Guid tenantId, Guid projectId, SecretPlatform platform, SecretKind kind,
        string name, string fileName, long size, string sha256, string? notes, Guid createdBy) => new()
    {
        TenantId = tenantId,
        ProjectId = projectId,
        Platform = platform,
        Kind = kind,
        Name = name.Trim(),
        FileName = fileName,
        SizeBytes = size,
        Sha256 = sha256,
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        CreatedByUserId = createdBy
    };

    public void SetKeyVersion(string version) => KeyVersion = version;

    public void Update(string name, string? notes)
    {
        Name = name.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public void RecordDownload(Guid userId, DateTime nowUtc)
    {
        LastDownloadedAtUtc = nowUtc;
        LastDownloadedByUserId = userId;
    }
}
