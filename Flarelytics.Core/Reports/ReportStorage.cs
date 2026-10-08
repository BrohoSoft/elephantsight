using Flarelytics.Core.Database.Entities;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Reports;

public class ReportsOptions
{
    public const string Section = "Reports";

    /// <summary>Dove stanno i report grezzi. In produzione è un volume che va nel backup.</summary>
    public string StorageDirectory { get; set; } = null!;
}

/// <summary>
/// I report grezzi su disco, così come arrivano dallo store (Apple li manda già
/// compressi in gzip).
/// </summary>
/// <remarks>
/// Un file per credenziale, tipo e giorno:
/// <c>{tenant}/{credenziale}/{tipo}/{aaaa-mm-gg}.gz</c>. Non sono cifrati come
/// le chiavi: sono dati di vendita, non credenziali, e la protezione è quella
/// del disco e dei backup. Il percorso salvato a database è relativo, così la
/// cartella si può spostare cambiando solo la configurazione.
/// </remarks>
public class ReportStorage(IOptions<ReportsOptions> options)
{
    private string Root => options.Value.StorageDirectory;

    public Task<string> WriteAsync(Guid tenantId, Guid credentialId, ReportKind kind, DateOnly date, byte[] content, CancellationToken ct) =>
        WriteAsync(Path.Combine(tenantId.ToString("N"), credentialId.ToString("N"), kind.ToString(), $"{date:yyyy-MM-dd}.gz"), content, ct);

    /// <summary>Un file di Google: il nome è quello del bucket, che dice già app e mese.</summary>
    public Task<string> WriteAsync(Guid tenantId, Guid credentialId, ReportKind kind, string objectName, byte[] content, CancellationToken ct) =>
        WriteAsync(Path.Combine(tenantId.ToString("N"), credentialId.ToString("N"), kind.ToString(), Path.GetFileName(objectName)), content, ct);

    private async Task<string> WriteAsync(string relative, byte[] content, CancellationToken ct)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Scrittura in un file temporaneo e rinomina: chi rielabora i report
        // non trova mai un file scritto a metà.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);

        return relative;
    }

    public Task<byte[]> ReadAsync(string relativePath, CancellationToken ct) =>
        File.ReadAllBytesAsync(Path.Combine(Root, relativePath), ct);

    public void DeleteCredential(Guid tenantId, Guid credentialId)
    {
        var directory = Path.Combine(Root, tenantId.ToString("N"), credentialId.ToString("N"));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
