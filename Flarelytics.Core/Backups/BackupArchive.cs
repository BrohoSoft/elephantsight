using System.Buffers.Binary;
using System.Text;

namespace Flarelytics.Core.Backups;

/// <summary>
/// Il contenuto di un backup (prima della cifratura): una sequenza di file con
/// nome, ciascuno a pezzi con la lunghezza davanti.
/// </summary>
/// <remarks>
/// Non tar: tar vuole la lunghezza di ogni file prima del contenuto, e il dump
/// del database arriva da <c>pg_dump</c> senza saperla. Così si scrive mentre
/// arriva, senza appoggiarlo in chiaro su disco.
/// </remarks>
public sealed class BackupArchiveWriter(Stream output)
{
    private static ReadOnlySpan<byte> Magic => "ESARCH1\n"u8;
    private const int ChunkSize = 256 * 1024;
    private bool _started;

    private void Start()
    {
        if (_started) return;
        output.Write(Magic);
        _started = true;
    }

    /// <summary>Un file: lo stream che si riceve va scritto e poi chiuso.</summary>
    public Stream Add(string name)
    {
        Start();
        var bytes = Encoding.UTF8.GetBytes(name);
        Span<byte> prefix = stackalloc byte[5];
        prefix[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(prefix[1..], bytes.Length);
        output.Write(prefix);
        output.Write(bytes);
        return new EntryStream(output);
    }

    public async Task AddAsync(string name, Stream content, CancellationToken ct)
    {
        await using var entry = Add(name);
        await content.CopyToAsync(entry, ChunkSize, ct);
    }

    /// <summary>La fine dell'archivio: senza, la lettura lo considera troncato.</summary>
    public void Complete()
    {
        Start();
        output.WriteByte(0);
    }

    private sealed class EntryStream(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _filled;
        private bool _closed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var n = Math.Min(data.Length, _buffer.Length - _filled);
                data[..n].CopyTo(_buffer.AsSpan(_filled));
                _filled += n;
                data = data[n..];
                if (_filled == _buffer.Length) FlushChunk();
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        private void FlushChunk()
        {
            if (_filled == 0) return;
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, _filled);
            output.Write(length);
            output.Write(_buffer, 0, _filled);
            _filled = 0;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                FlushChunk();
                output.Write([0, 0, 0, 0]);
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public static class BackupArchiveReader
{
    /// <summary>
    /// Legge i file uno dopo l'altro: <paramref name="onEntry"/> riceve nome e
    /// contenuto (da leggere lì; quello che non legge si salta). Un archivio
    /// senza la fine scritta si rifiuta.
    /// </summary>
    public static async Task ReadAsync(Stream input, Func<string, Stream, Task> onEntry, CancellationToken ct)
    {
        var magic = new byte[8];
        await input.ReadExactlyAsync(magic, ct);
        if (!magic.AsSpan().SequenceEqual("ESARCH1\n"u8)) throw new InvalidDataException("Il contenuto del backup non è nel formato atteso.");

        var head = new byte[4];
        while (true)
        {
            var kind = new byte[1];
            if (await input.ReadAsync(kind, ct) == 0) throw new InvalidDataException("Il backup è troncato.");
            if (kind[0] == 0) return;
            if (kind[0] != 1) throw new InvalidDataException("Il contenuto del backup non è nel formato atteso.");

            await input.ReadExactlyAsync(head, ct);
            var nameLength = BinaryPrimitives.ReadInt32BigEndian(head);
            if (nameLength is < 1 or > 4096) throw new InvalidDataException("Nome di un file del backup non valido.");
            var name = new byte[nameLength];
            await input.ReadExactlyAsync(name, ct);

            var entry = new ChunkedReadStream(input);
            await onEntry(Encoding.UTF8.GetString(name), entry);
            await entry.CopyToAsync(Stream.Null, ct);
        }
    }

    private sealed class ChunkedReadStream(Stream input) : Stream
    {
        private readonly byte[] _head = new byte[4];
        private int _remaining;
        private bool _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (_done || destination.Length == 0) return 0;
            if (_remaining == 0)
            {
                input.ReadExactly(_head);
                _remaining = BinaryPrimitives.ReadInt32BigEndian(_head);
                if (_remaining < 0) throw new InvalidDataException("Il contenuto del backup non è nel formato atteso.");
                if (_remaining == 0)
                {
                    _done = true;
                    return 0;
                }
            }
            var n = input.Read(destination[..Math.Min(destination.Length, _remaining)]);
            if (n == 0) throw new InvalidDataException("Il backup è troncato.");
            _remaining -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
