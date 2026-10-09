namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Una persona che accede al pannello. Può appartenere a più tenant attraverso
/// <see cref="Membership"/>.
/// </summary>
public class User : BaseEntity
{
    /// <summary>
    /// Costo di BCrypt. 12 vuol dire qualche centinaio di millisecondi per
    /// tentativo: impercettibile per chi entra, proibitivo per chi prova
    /// password a raffica.
    /// </summary>
    private const int WorkFactor = 12;

    public string Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public DateTime? EmailConfirmedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }

    /// <summary>
    /// Cambia ogni volta che cambia qualcosa che deve invalidare gli access
    /// token già emessi: oggi la password. Il token lo porta con sé e la
    /// validazione lo confronta con questo.
    /// </summary>
    public string SecurityStamp { get; private set; } = NewStamp();

    public bool IsEmailConfirmed => EmailConfirmedAtUtc is not null;

    /// <summary>
    /// Il seme TOTP, cifrato con <c>FieldProtector</c>. Valorizzato già durante
    /// la configurazione, ma la 2FA vale solo da <see cref="TotpEnabledAtUtc"/>:
    /// chi inizia la configurazione e non la finisce non si chiude fuori.
    /// </summary>
    public string? TotpSecretProtected { get; private set; }
    public DateTime? TotpEnabledAtUtc { get; private set; }

    /// <summary>
    /// L'ultimo intervallo di 30 secondi per cui è stato accettato un codice.
    /// Un codice vale per un solo accesso: chi lo vede sopra la spalla non lo
    /// può riusare nei secondi che gli restano.
    /// </summary>
    public long? TotpLastUsedStep { get; private set; }

    /// <summary>Hash SHA-256 dei codici di recupero ancora inutilizzati.</summary>
    public List<string> RecoveryCodeHashes { get; private set; } = [];

    public bool IsTwoFactorEnabled => TotpEnabledAtUtc is not null;

    /// <summary>
    /// Amministra l'installazione (SMTP, app social, altri amministratori), al
    /// di sopra delle organizzazioni: un owner di un'organizzazione cliente non
    /// tocca l'istanza. Lo è chi ha fatto l'installazione, e chi nomina lui.
    /// </summary>
    public bool IsInstanceAdmin { get; private set; }

    public void SetInstanceAdmin(bool value) => IsInstanceAdmin = value;

    private User() { }

    public static User Create(string email, string password, string fullName) => new()
    {
        Email = NormalizeEmail(email),
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor),
        FullName = fullName.Trim()
    };

    /// <summary>
    /// La forma con cui un'email si salva e si cerca. Le email si confrontano
    /// sempre normalizzate: l'indice unico sta su questa forma, quindi
    /// "Mario@x.it" e "mario@x.it" sono lo stesso account.
    /// </summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public bool VerifyPassword(string password) => BCrypt.Net.BCrypt.Verify(password, PasswordHash);

    public void ChangePassword(string newPassword)
    {
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, WorkFactor);
        SecurityStamp = NewStamp();
    }

    public void ConfirmEmail(DateTime nowUtc) => EmailConfirmedAtUtc ??= nowUtc;

    public void Rename(string fullName) => FullName = fullName.Trim();

    /// <summary>Un seme nuovo, non ancora attivo. Con la 2FA già attiva non si cambia da qui: prima si disattiva.</summary>
    public void BeginTwoFactorSetup(string protectedSecret)
    {
        if (IsTwoFactorEnabled) throw new InvalidOperationException("La 2FA è già attiva.");
        TotpSecretProtected = protectedSecret;
    }

    public void EnableTwoFactor(DateTime nowUtc, long usedStep, IEnumerable<string> recoveryCodeHashes)
    {
        TotpEnabledAtUtc = nowUtc;
        TotpLastUsedStep = usedStep;
        RecoveryCodeHashes = [.. recoveryCodeHashes];
    }

    public void DisableTwoFactor()
    {
        TotpSecretProtected = null;
        TotpEnabledAtUtc = null;
        TotpLastUsedStep = null;
        RecoveryCodeHashes = [];
    }

    public void ReplaceRecoveryCodes(IEnumerable<string> hashes) => RecoveryCodeHashes = [.. hashes];

    public void MarkTotpStepUsed(long step) => TotpLastUsedStep = step;

    /// <summary>Consuma un codice di recupero, che vale una volta sola.</summary>
    public bool UseRecoveryCode(string hash)
    {
        if (!RecoveryCodeHashes.Contains(hash)) return false;

        // Lista nuova e non Remove sulla stessa: EF si accorge del cambio di
        // una colonna array confrontando i valori, ed è più sicuro dargli
        // un'istanza diversa.
        RecoveryCodeHashes = RecoveryCodeHashes.Where(h => h != hash).ToList();
        return true;
    }

    public void RegisterLogin(DateTime nowUtc) => LastLoginAtUtc = nowUtc;

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}
