namespace Flarelytics.Core.Database.Entities;

/// <summary>
/// L'organizzazione cliente: il confine dei dati. Progetti, credenziali e
/// abbonamento appartengono a un tenant, non a una persona.
/// </summary>
/// <remarks>
/// Non implementa <see cref="ITenantOwned"/>: la lista dei tenant di un utente
/// si legge prima di aver scelto in quale si sta lavorando, e un filtro sul
/// tenant corrente la renderebbe sempre vuota. L'accesso passa da
/// <see cref="Membership"/>.
/// </remarks>
public class Tenant : BaseEntity
{
    public string Name { get; private set; } = null!;

    private Tenant() { }

    public static Tenant Create(string name) => new() { Name = name.Trim() };

    public void Rename(string name) => Name = name.Trim();
}
