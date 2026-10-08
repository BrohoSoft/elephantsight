namespace Flarelytics.Core.Database.Entities;

public enum Store
{
    AppStore = 0,
    GooglePlay = 1
}

public enum CredentialStatus
{
    /// <summary>L'ultima verifica è riuscita del tutto.</summary>
    Valid = 0,

    /// <summary>
    /// La chiave è buona ma non ha ancora accesso a tutto. È lo stato normale
    /// di un service account Google appena invitato in Play Console: i permessi
    /// possono metterci ore ad attivarsi.
    /// </summary>
    Limited = 1,

    /// <summary>Lo store non accetta più la chiave: revocata o cancellata dal cliente.</summary>
    Invalid = 2
}

/// <summary>
/// Una chiave di accesso a uno store, caricata da un tenant.
/// </summary>
/// <remarks>
/// <para>Qui ci sono solo i <b>metadati</b>, che non danno accesso a niente:
/// Key ID, Issuer ID, email del service account. Il segreto vero (il .p8 o il
/// JSON) sta cifrato su disco, in un file che <see cref="Secrets.SecretVault"/>
/// lega a questo id e a questo tenant.</para>
///
/// <para>Appartiene al tenant e non al progetto: una chiave Apple vale per tutte
/// le app del team, un service account per tutto l'account sviluppatore. Il
/// progetto la usa attraverso <see cref="ProjectApp"/>.</para>
/// </remarks>
public class StoreCredential : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Store Store { get; private set; }
    public string Label { get; private set; } = null!;

    /// <summary>
    /// SHA-256 della chiave pubblica. Impedisce di caricare due volte la stessa
    /// chiave nello stesso tenant senza dover confrontare i segreti.
    /// </summary>
    public string Fingerprint { get; private set; } = null!;

    // App Store Connect
    public string? AppleKeyId { get; private set; }
    public string? AppleIssuerId { get; private set; }
    public string? AppleVendorNumber { get; private set; }

    // Google Play
    public string? GoogleClientEmail { get; private set; }
    public string? GoogleReportsBucket { get; private set; }

    /// <summary>Versione della chiave master con cui è cifrato il file: serve a sapere cosa ricifrare in una rotazione.</summary>
    public string KeyVersion { get; private set; } = null!;

    public CredentialStatus Status { get; private set; }

    /// <summary>Qualcuno ha premuto "Sincronizza ora": il worker la prende al giro successivo.</summary>
    public DateTime? SyncRequestedAtUtc { get; private set; }
    public DateTime? LastSyncCompletedAtUtc { get; private set; }

    /// <summary>L'errore dell'ultima sincronizzazione, da mostrare. Null se è andata bene.</summary>
    public string? LastSyncError { get; private set; }
    public string? StatusMessage { get; private set; }
    public DateTime? LastVerifiedAtUtc { get; private set; }

    private StoreCredential() { }

    public static StoreCredential ForAppStore(
        Guid tenantId, string label, string fingerprint, string keyId, string issuerId, string? vendorNumber) => new()
    {
        TenantId = tenantId,
        Store = Store.AppStore,
        Label = label.Trim(),
        Fingerprint = fingerprint,
        AppleKeyId = keyId,
        AppleIssuerId = issuerId,
        AppleVendorNumber = vendorNumber
    };

    public static StoreCredential ForGooglePlay(
        Guid tenantId, string label, string fingerprint, string clientEmail, string? reportsBucket) => new()
    {
        TenantId = tenantId,
        Store = Store.GooglePlay,
        Label = label.Trim(),
        Fingerprint = fingerprint,
        GoogleClientEmail = clientEmail,
        GoogleReportsBucket = reportsBucket
    };

    public void Rename(string label) => Label = label.Trim();

    public void SetKeyVersion(string version) => KeyVersion = version;

    public void RequestSync(DateTime nowUtc) => SyncRequestedAtUtc = nowUtc;

    public void RecordSync(DateTime nowUtc, string? error)
    {
        SyncRequestedAtUtc = null;
        LastSyncCompletedAtUtc = nowUtc;
        LastSyncError = error;
    }

    public void RecordVerification(CredentialStatus status, string? message, DateTime nowUtc)
    {
        Status = status;
        StatusMessage = message;
        LastVerifiedAtUtc = nowUtc;
    }
}
