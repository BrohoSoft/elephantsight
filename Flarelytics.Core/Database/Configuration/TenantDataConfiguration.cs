using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flarelytics.Core.Database.Configuration;

// Le entità di questo file sono tutte ITenantOwned: ogni tabella nuova che si
// aggiunge qui vuole anche RowLevelSecurity.Enable nella sua migration.

public class StoreCredentialConfiguration : IEntityTypeConfiguration<StoreCredential>
{
    public void Configure(EntityTypeBuilder<StoreCredential> b)
    {
        b.Property(c => c.Label).HasMaxLength(100);
        b.Property(c => c.Fingerprint).HasMaxLength(64);
        b.Property(c => c.AppleKeyId).HasMaxLength(20);
        b.Property(c => c.AppleIssuerId).HasMaxLength(36);
        b.Property(c => c.AppleVendorNumber).HasMaxLength(20);
        b.Property(c => c.GoogleClientEmail).HasMaxLength(255);
        b.Property(c => c.GoogleReportsBucket).HasMaxLength(100);
        b.Property(c => c.KeyVersion).HasMaxLength(32);
        b.Property(c => c.StatusMessage).HasMaxLength(1000);
        b.Property(c => c.LastSyncError).HasMaxLength(1000);

        b.HasIndex(c => new { c.TenantId, c.Fingerprint }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.Property(p => p.Name).HasMaxLength(100);
        b.Property(p => p.Description).HasMaxLength(500);

        b.HasIndex(p => new { p.TenantId, p.Name }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);

        b.HasMany(p => p.Apps).WithOne().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Apps).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class ProjectAppConfiguration : IEntityTypeConfiguration<ProjectApp>
{
    public void Configure(EntityTypeBuilder<ProjectApp> b)
    {
        b.Property(a => a.ExternalAppId).HasMaxLength(200);
        b.Property(a => a.DisplayName).HasMaxLength(200);

        // Un'app per store in ogni progetto.
        b.HasIndex(a => new { a.ProjectId, a.Store }).IsUnique();
        b.HasIndex(a => a.CredentialId);

        // Restrict: una credenziale in uso non si cancella di nascosto. L'API
        // risponde 409 con i progetti che la usano prima di arrivare qui.
        b.HasOne(a => a.Credential).WithMany().HasForeignKey(a => a.CredentialId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReportFileConfiguration : IEntityTypeConfiguration<ReportFile>
{
    public void Configure(EntityTypeBuilder<ReportFile> b)
    {
        b.Property(f => f.RelativePath).HasMaxLength(400);
        b.Property(f => f.Scope).HasMaxLength(200).HasDefaultValue("");
        b.Property(f => f.ContentHash).HasMaxLength(64);

        b.HasIndex(f => new { f.CredentialId, f.Kind, f.ReportDate, f.Scope }).IsUnique();
        b.HasOne<StoreCredential>().WithMany().HasForeignKey(f => f.CredentialId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Nello schema <c>metrics</c>: è la tabella che crescerà, e tenerla a parte
/// la rende facile da spostare, partizionare o salvare con un ritmo diverso.
/// </summary>
public class DailyAppMetricConfiguration : IEntityTypeConfiguration<DailyAppMetric>
{
    public const string Schema = "metrics";

    public void Configure(EntityTypeBuilder<DailyAppMetric> b)
    {
        b.ToTable(nameof(DailyAppMetric), Schema);
        b.HasKey(m => new { m.TenantId, m.Store, m.AppId, m.Date, m.CountryCode });
        b.Property(m => m.AppId).HasMaxLength(200);
        b.Property(m => m.CountryCode).HasMaxLength(2).IsFixedLength();

        // Le letture della dashboard: tutte le app di un tenant in un periodo.
        b.HasIndex(m => new { m.TenantId, m.Date });
        b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppleAppSkuConfiguration : IEntityTypeConfiguration<AppleAppSku>
{
    public void Configure(EntityTypeBuilder<AppleAppSku> b)
    {
        b.HasKey(x => new { x.TenantId, x.Sku });
        b.Property(x => x.Sku).HasMaxLength(200);
        b.Property(x => x.AppleId).HasMaxLength(20);
        b.HasIndex(x => x.CredentialId);
        b.HasOne<StoreCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}
