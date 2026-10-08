using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Stores;

public enum VerificationOutcome
{
    /// <summary>La chiave funziona e vede quello che deve vedere.</summary>
    Ok,

    /// <summary>
    /// La chiave è autentica ma le mancano dei permessi, o lo store non ha
    /// ancora propagato l'invito. Si salva lo stesso, con un avviso.
    /// </summary>
    Limited,

    /// <summary>Lo store rifiuta la chiave: revocata, sbagliata, di un altro account. Non si salva.</summary>
    Rejected,

    /// <summary>Lo store non ha risposto. Non dice niente sulla chiave.</summary>
    Unreachable
}

/// <param name="Message">Cosa dire all'utente: perché è limitata o rifiutata, e cosa fare.</param>
public record VerificationResult(VerificationOutcome Outcome, string? Message = null)
{
    public static readonly VerificationResult Ok = new(VerificationOutcome.Ok);
}

/// <param name="ExternalId">L'id da salvare su <see cref="ProjectApp.ExternalAppId"/>.</param>
/// <param name="Name">Il nome dell'app sullo store.</param>
/// <param name="BundleId">Bundle id (Apple) o package name (Google), da mostrare per riconoscerla.</param>
public record StoreAppInfo(string ExternalId, string Name, string BundleId);

/// <summary>
/// Quello che serve sapere di uno store per collegarlo: se una chiave funziona
/// e quali app vede.
/// </summary>
/// <remarks>
/// Ricevono il segreto in chiaro come byte e non lo conservano: chi chiama lo
/// ha letto da <see cref="Secrets.SecretVault"/> e lo azzera subito dopo.
/// </remarks>
public interface IStoreGateway
{
    Store Store { get; }

    Task<VerificationResult> VerifyAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct);

    Task<IReadOnlyList<StoreAppInfo>> ListAppsAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct);
}

/// <summary>Lo store risponde, ma con un errore: il messaggio è per l'utente.</summary>
public class StoreAccessException(string message) : Exception(message);
