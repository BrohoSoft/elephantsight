using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flarelytics.Core.Database.Configuration;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(u => u.Email).HasMaxLength(255);
        b.Property(u => u.PasswordHash).HasMaxLength(100);
        b.Property(u => u.FullName).HasMaxLength(200);
        b.Property(u => u.SecurityStamp).HasMaxLength(64);
        b.Property(u => u.TotpSecretProtected).HasMaxLength(200);

        // Il valore predefinito serve alla migration più che al codice: senza,
        // aggiungere la colonna NOT NULL a una tabella che ha già degli utenti
        // fallisce, perché le righe esistenti non avrebbero un valore.
        b.Property(u => u.RecoveryCodeHashes).HasDefaultValueSql("'{}'");

        // Sulla forma normalizzata: User.NormalizeEmail è l'unica strada per
        // scrivere e cercare un'email, quindi basta un indice semplice.
        b.HasIndex(u => u.Email).IsUnique();
    }
}

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(t => t.Name).HasMaxLength(100);
    }
}

public class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> b)
    {
        b.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        b.HasIndex(m => m.UserId);

        b.HasOne(m => m.Tenant).WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class EmailTokenConfiguration : IEntityTypeConfiguration<EmailToken>
{
    public void Configure(EntityTypeBuilder<EmailToken> b)
    {
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => new { t.UserId, t.Purpose });
        b.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.Property(i => i.Email).HasMaxLength(255);
        b.Property(i => i.TokenHash).HasMaxLength(64);
        b.HasIndex(i => i.TokenHash).IsUnique();
        b.HasIndex(i => new { i.TenantId, i.Email });

        b.HasOne(i => i.Tenant).WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class LoginChallengeConfiguration : IEntityTypeConfiguration<LoginChallenge>
{
    public void Configure(EntityTypeBuilder<LoginChallenge> b)
    {
        b.Property(c => c.TokenHash).HasMaxLength(64);
        b.HasIndex(c => c.TokenHash).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> b)
    {
        b.HasKey(r => new { r.Currency, r.Date });
        b.Property(r => r.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(r => r.UnitsPerEuro).HasPrecision(18, 6);
    }
}
