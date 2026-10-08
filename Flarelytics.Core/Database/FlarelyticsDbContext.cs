using System.Linq.Expressions;
using System.Reflection;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Flarelytics.Core.Database;

/// <summary>
/// Il modello, con l'isolamento dei tenant già applicato.
/// </summary>
/// <remarks>
/// <para><b>Niente proprietà DbSet</b>: gli insiemi si prendono con
/// <c>Set&lt;T&gt;()</c> e le entità entrano nel modello solo attraverso le
/// <c>IEntityTypeConfiguration</c> in <c>Database/Configuration</c>. Un'entità
/// senza configuration non esiste.</para>
///
/// <para>Ogni entità <see cref="ITenantOwned"/> riceve un filtro su
/// <see cref="CurrentTenantId"/>. EF lo valuta per ogni istanza del context,
/// quindi ogni richiesta vede il proprio tenant.</para>
/// </remarks>
public class FlarelyticsDbContext(DbContextOptions<FlarelyticsDbContext> options, TenantContext tenant)
    : DbContext(options)
{
    /// <summary>Letto dal filtro globale: deve essere una proprietà del context perché EF lo parametrizzi per istanza.</summary>
    public Guid? CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;

            if (typeof(BaseEntity).IsAssignableFrom(clr))
            {
                // L'id lo genera BaseEntity. Senza questa riga EF, trovando una
                // chiave Guid già valorizzata su un'entità nuova raggiunta da
                // una navigazione, la scambierebbe per esistente e farebbe una
                // UPDATE invece di una INSERT.
                modelBuilder.Entity(clr).Property(nameof(BaseEntity.Id)).ValueGeneratedNever();
            }

            if (typeof(ITenantOwned).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr).HasQueryFilter(TenantFilter(clr));
            }
        }
    }

    /// <summary><c>e =&gt; e.TenantId == this.CurrentTenantId</c>, costruito per il tipo indicato.</summary>
    private LambdaExpression TenantFilter(Type clr)
    {
        var e = Expression.Parameter(clr, "e");
        var tenantId = Expression.Convert(
            Expression.Property(e, nameof(ITenantOwned.TenantId)), typeof(Guid?));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

        return Expression.Lambda(Expression.Equal(tenantId, current), e);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Stamp()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAtUtc = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    break;
            }

            // Un'entità del tenant non si scrive mai su un tenant diverso da
            // quello della richiesta. La RLS la fermerebbe comunque, ma con un
            // errore del database che non dice niente: qui il messaggio è chiaro.
            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Entity is ITenantOwned owned
                && owned.TenantId != CurrentTenantId)
            {
                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} appartiene a un tenant diverso da quello della richiesta.");
            }
        }
    }
}
