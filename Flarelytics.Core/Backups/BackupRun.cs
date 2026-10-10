using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Flarelytics.Core.Backups;

/// <summary>
/// Un backup tentato: quando, se a mano o programmato, il file e l'esito. Serve
/// al pannello (lo storico) e al worker (l'appuntamento è già stato fatto?).
/// </summary>
/// <remarks>Non è del tenant: è dell'istanza, come <c>InstanceSetting</c>.</remarks>
public class BackupRun
{
    public Guid Id { get; private set; }
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public bool Manual { get; private set; }
    public string? FileName { get; private set; }
    public long? SizeBytes { get; private set; }
    public string? Error { get; private set; }

    private BackupRun() { }

    public static BackupRun Start(bool manual, DateTime nowUtc) => new() { Id = Guid.CreateVersion7(), StartedAtUtc = nowUtc, Manual = manual };

    public void Succeed(string fileName, long size, DateTime nowUtc)
    {
        FileName = fileName;
        SizeBytes = size;
        FinishedAtUtc = nowUtc;
    }

    public void Fail(string error, DateTime nowUtc)
    {
        Error = error.Length > 2000 ? error[..2000] : error;
        FinishedAtUtc = nowUtc;
    }
}

public class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b)
    {
        b.HasKey(r => r.Id);
        b.Property(r => r.FileName).HasMaxLength(200);
        b.Property(r => r.Error).HasMaxLength(2000);
        b.HasIndex(r => r.StartedAtUtc);
    }
}
