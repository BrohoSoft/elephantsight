using System.Security.Cryptography;
using System.Text;

namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Una chiave con cui un programma esterno (un CMS, uno script, un'automazione)
/// manda post nella coda "Da programmare" dell'organizzazione.
/// </summary>
/// <remarks>
/// <para><b>Non è <see cref="ITenantOwned"/></b>, come <see cref="Membership"/>:
/// è la tabella da cui si scopre il tenant di una richiesta, quindi si legge
/// prima di averlo impostato. Le rotte del pannello filtrano per tenant a mano.</para>
///
/// <para>A database c'è solo l'hash SHA-256: la chiave è casuale a 256 bit,
/// quindi un hash lento non aggiungerebbe niente, e chi legge la tabella non
/// può usarla. Si mostra una volta sola, alla creazione; il prefisso resta per
/// riconoscerla nell'elenco.</para>
/// </remarks>
public class ApiKey : BaseEntity
{
    public const string Marker = "wsk_";

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>I primi caratteri della chiave ("wsk_AbC1…"), per riconoscerla senza conservarla.</summary>
    public string Prefix { get; private set; } = null!;

    public string KeyHash { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public DateTime? LastUsedAtUtc { get; private set; }

    private ApiKey() { }

    /// <summary>Una chiave nuova. La stringa restituita è l'unica copia: va data all'utente e dimenticata.</summary>
    public static (ApiKey Key, string Secret) Create(Guid tenantId, string name, Guid createdBy)
    {
        var secret = Marker + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (new ApiKey { TenantId = tenantId, Name = name.Trim(), Prefix = secret[..12], KeyHash = Hash(secret), CreatedByUserId = createdBy }, secret);
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Si aggiorna al massimo una volta al minuto: una scrittura per ogni richiesta sarebbe solo rumore.</summary>
    public bool TouchIfStale(DateTime nowUtc)
    {
        if (LastUsedAtUtc is { } last && nowUtc - last < TimeSpan.FromMinutes(1)) return false;
        LastUsedAtUtc = nowUtc;
        return true;
    }
}
