namespace Flarelytics.Core.Tenancy;

/// <summary>
/// Il tenant su cui sta lavorando la richiesta (o il job) corrente.
/// </summary>
/// <remarks>
/// Scoped: uno per richiesta. Lo valorizza chi ha verificato che l'utente ne
/// fa parte — nell'API il filtro sulle rotte <c>/orgs/{orgId}</c>, nel worker
/// il job prima di toccare i dati — e da lì lo leggono due cose:
/// <list type="bullet">
/// <item>il filtro globale di EF, che aggiunge <c>TenantId = @corrente</c> a
/// ogni query sulle entità del tenant;</item>
/// <item><see cref="TenantConnectionInterceptor"/>, che lo scrive sulla
/// connessione per la Row-Level Security di PostgreSQL.</item>
/// </list>
/// Finché è vuoto, le tabelle dei tenant appaiono vuote: il caso "mi sono
/// dimenticato di impostarlo" non mostra niente, invece di mostrare tutto.
/// </remarks>
public class TenantContext
{
    private static readonly AsyncLocal<Guid?> AmbientTenant = new();

    public Guid? TenantId { get; private set; }

    /// <summary>
    /// Il tenant su cui sta lavorando il flusso corrente (la richiesta, o il
    /// giro del worker per quel tenant), senza passare dal container: lo usa il
    /// log a database per dire a quale organizzazione si riferisce un messaggio.
    /// Solo per questo: i dati si leggono sempre con <see cref="TenantId"/>.
    /// </summary>
    public static Guid? Ambient => AmbientTenant.Value;

    public void Set(Guid tenantId)
    {
        // Cambiare tenant a metà richiesta vuol dire quasi certamente un errore
        // di chi chiama: meglio fermarsi che mescolare i dati di due clienti.
        if (TenantId is not null && TenantId != tenantId)
        {
            throw new InvalidOperationException("Il tenant della richiesta è già stato impostato su un altro valore.");
        }

        TenantId = tenantId;
        AmbientTenant.Value = tenantId;
    }
}
