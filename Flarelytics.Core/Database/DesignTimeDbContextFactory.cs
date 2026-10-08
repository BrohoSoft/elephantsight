using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Flarelytics.Core.Database;

/// <summary>
/// Usata solo da <c>dotnet ef</c> per generare le migration: non si collega a
/// niente, quindi la stringa di connessione è solo un segnaposto.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FlarelyticsDbContext>
{
    public FlarelyticsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FlarelyticsDbContext>()
            .UseNpgsql("Host=localhost;Database=flarelytics_design")
            .Options;

        return new FlarelyticsDbContext(options, new TenantContext());
    }
}
