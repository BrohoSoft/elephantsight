namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// Identificativo e date comuni a tutte le entità.
/// </summary>
/// <remarks>
/// L'id è un Guid v7, generato dal codice e non dal database: è ordinato nel
/// tempo, quindi gli indici restano compatti come con un intero progressivo, ed
/// esiste già prima del salvataggio. Serve, per esempio, a cifrare una
/// credenziale legando il file al suo id prima ancora di scrivere la riga.
/// </remarks>
public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// Un'entità che appartiene a un tenant.
/// </summary>
/// <remarks>
/// Implementare questa interfaccia ha due effetti, ed entrambi sono voluti:
/// <list type="bullet">
/// <item><see cref="FlarelyticsDbContext"/> aggiunge da solo il filtro sul
/// tenant corrente a ogni query;</item>
/// <item>la migration deve accendere la Row-Level Security sulla tabella (vedi
/// <see cref="RowLevelSecurity"/>). Questo passo <b>non</b> è automatico: una
/// tabella nuova senza politica è protetta solo dal filtro di EF.</item>
/// </list>
/// </remarks>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
