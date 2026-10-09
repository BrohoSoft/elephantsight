using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flarelytics.Core.Database.Configuration;

// Anche queste sono ITenantOwned: ogni tabella vuole RowLevelSecurity.Enable
// nella sua migration.

public class SocialAccountConfiguration : IEntityTypeConfiguration<SocialAccount>
{
    public void Configure(EntityTypeBuilder<SocialAccount> b)
    {
        b.Property(a => a.ExternalId).HasMaxLength(300);
        b.Property(a => a.Name).HasMaxLength(200);
        b.Property(a => a.Handle).HasMaxLength(300);
        b.Property(a => a.ServerUrl).HasMaxLength(300);
        b.Property(a => a.ProtectedSecret).HasMaxLength(4000);
        b.Property(a => a.StatusMessage).HasMaxLength(1000);

        // Lo stesso account collegato due volte diventa un ricollegamento.
        b.HasIndex(a => new { a.TenantId, a.Network, a.ExternalId }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SocialPostConfiguration : IEntityTypeConfiguration<SocialPost>
{
    public void Configure(EntityTypeBuilder<SocialPost> b)
    {
        b.Property(p => p.Text).HasMaxLength(10000);
        b.Property(p => p.ExternalRef).HasMaxLength(200);

        b.HasIndex(p => new { p.TenantId, p.ScheduledAtUtc });
        b.HasIndex(p => new { p.TenantId, p.IsInbox });
        b.HasIndex(p => new { p.TenantId, p.ExternalRef }).IsUnique().HasFilter("\"ExternalRef\" IS NOT NULL");
        b.HasOne<ApiKey>().WithMany().HasForeignKey(p => p.ApiKeyId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        // Cancellato il progetto, il post resta sul calendario senza etichetta.
        b.HasOne<Project>().WithMany().HasForeignKey(p => p.ProjectId).OnDelete(DeleteBehavior.SetNull);

        b.HasMany(p => p.Targets).WithOne().HasForeignKey(t => t.PostId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Targets).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasMany(p => p.Media).WithOne().HasForeignKey(m => m.PostId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Media).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class SocialPostTargetConfiguration : IEntityTypeConfiguration<SocialPostTarget>
{
    public void Configure(EntityTypeBuilder<SocialPostTarget> b)
    {
        b.Property(t => t.AccountName).HasMaxLength(200);
        b.Property(t => t.TextOverride).HasMaxLength(10000);
        b.Property(t => t.ExternalId).HasMaxLength(300);
        b.Property(t => t.ExternalUrl).HasMaxLength(500);
        b.Property(t => t.Error).HasMaxLength(2000);
        b.Property(t => t.ProgressState).HasMaxLength(300);

        // Il giro del worker: i post in attesa di un tenant.
        b.HasIndex(t => new { t.TenantId, t.Status });
        b.HasIndex(t => t.AccountId);
        // L'importazione cerca se un post della rete c'è già (pubblicato da qui o importato prima).
        b.HasIndex(t => new { t.TenantId, t.Network, t.ExternalId });
        b.HasOne<SocialAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SocialMediaConfiguration : IEntityTypeConfiguration<SocialMedia>
{
    public void Configure(EntityTypeBuilder<SocialMedia> b)
    {
        b.Property(m => m.FileName).HasMaxLength(255);
        b.Property(m => m.AltText).HasMaxLength(1500);

        b.HasIndex(m => m.PostId);
        b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
