using System.Security.Cryptography;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Secrets;
using Microsoft.Extensions.Options;

namespace Flarelytics.Core.Backups;

/// <summary>
/// Il ripristino, dal comando <c>restore</c> dell'immagine (a istanza ferma,
/// mai dal pannello). Prima legge tutto il file per verificarlo (password,
/// integrità): solo se è intero scrive chiavi, credenziali, report e database.
/// </summary>
public class BackupRestorer(PgTools pg, IOptions<SecretsOptions> secrets, IOptions<ReportsOptions> reports)
{
    public record Summary(int Files, long Bytes, bool HasDatabase, IReadOnlyList<string> Areas);

    /// <summary>Legge tutto senza scrivere niente. Password sbagliata o file alterato: <see cref="CryptographicException"/>.</summary>
    public async Task<Summary> VerifyAsync(string path, string password, CancellationToken ct)
    {
        var files = 0;
        long bytes = 0;
        var database = false;
        var areas = new SortedSet<string>();
        await using var input = BackupCipher.Decrypt(File.OpenRead(path), password);
        await BackupArchiveReader.ReadAsync(input, async (name, content) =>
        {
            var counter = new CountingStream();
            await content.CopyToAsync(counter, ct);
            if (name == "database.dump") database = true;
            else if (name != "manifest.json")
            {
                files++;
                areas.Add(name.Split('/')[0]);
            }
            bytes += counter.Count;
        }, ct);
        return new Summary(files, bytes, database, areas.ToList());
    }

    /// <summary>
    /// Scrive tutto al suo posto: i file sopra quelli che ci sono, il database
    /// al posto di quello attuale (<c>pg_restore --clean</c>, in una transazione).
    /// </summary>
    public async Task<Summary> RestoreAsync(string path, string password, string connectionString, CancellationToken ct)
    {
        var summary = await VerifyAsync(path, password, ct);
        var roots = new Dictionary<string, string>
        {
            ["keys"] = Path.GetFullPath(secrets.Value.KeysDirectory),
            ["secrets"] = Path.GetFullPath(secrets.Value.StorageDirectory),
            ["reports"] = Path.GetFullPath(reports.Value.StorageDirectory),
        };

        await using var input = BackupCipher.Decrypt(File.OpenRead(path), password);
        await BackupArchiveReader.ReadAsync(input, async (name, content) =>
        {
            if (name == "database.dump")
            {
                await pg.RestoreAsync(connectionString, content, ct);
                return;
            }
            var slash = name.IndexOf('/');
            if (slash <= 0 || !roots.TryGetValue(name[..slash], out var root)) return;

            // Un nome con ../ non esce dalla sua cartella.
            var target = Path.GetFullPath(Path.Combine(root, name[(slash + 1)..]));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = File.Create(target);
            await content.CopyToAsync(file, ct);
        }, ct);
        return summary;
    }

    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Count += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
