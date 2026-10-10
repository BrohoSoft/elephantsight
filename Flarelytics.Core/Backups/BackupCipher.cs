using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Flarelytics.Core.Backups;

/// <summary>
/// Cifra un backup con una password, in streaming: il backup non passa mai in
/// chiaro dal disco, e si può fare grande quanto serve.
/// </summary>
/// <remarks>
/// <para>Formato: <c>ESBK</c>, versione, iterazioni (PBKDF2-SHA256), sale,
/// dimensione dei blocchi; poi blocchi AES-256-GCM (testo cifrato + tag). Il
/// nonce è l'indice del blocco (la chiave è nuova per ogni file, grazie al
/// sale), e nei dati associati ci sono l'intestazione, l'indice e se è
/// l'ultimo: blocchi scambiati, tolti o un file troncato non si decifrano.</para>
///
/// <para>Non usa la chiave master dell'istanza: il backup la contiene, e il
/// ripristino su un server nuovo parte proprio senza.</para>
/// </remarks>
public static class BackupCipher
{
    public static ReadOnlySpan<byte> Magic => "ESBK"u8;
    public const byte FormatVersion = 1;
    public const int DefaultIterations = 600_000;
    public const int DefaultBlockSize = 1024 * 1024;
    public const int SaltSize = 16;
    public const int TagSize = 16;
    public const int HeaderSize = 4 + 1 + 4 + SaltSize + 4;

    /// <summary>Uno stream in cui si scrive il chiaro; chiuderlo scrive l'ultimo blocco.</summary>
    public static Stream Encrypt(Stream output, string password, int iterations = DefaultIterations, int blockSize = DefaultBlockSize)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), iterations);
        RandomNumberGenerator.Fill(header.AsSpan(9, SaltSize));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(9 + SaltSize), blockSize);
        output.Write(header);
        return new EncryptingStream(output, DeriveKey(password, header.AsSpan(9, SaltSize), iterations), header, blockSize);
    }

    /// <summary>Uno stream da cui si legge il chiaro. Password sbagliata o file alterato: <see cref="CryptographicException"/>.</summary>
    public static Stream Decrypt(Stream input, string password)
    {
        var header = new byte[HeaderSize];
        if (input.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Non è un backup di ElephantSight.");
        if (header[4] != FormatVersion) throw new CryptographicException($"Formato di backup {header[4]} non supportato.");
        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));
        var blockSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(9 + SaltSize));
        if (iterations is < 1 or > 10_000_000 || blockSize is < 1 or > 64 * 1024 * 1024) throw new CryptographicException("Intestazione del backup non valida.");
        return new DecryptingStream(input, DeriveKey(password, header.AsSpan(9, SaltSize), iterations), header, blockSize);
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static void Nonce(Span<byte> nonce, long index)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
    }

    private static byte[] AssociatedData(byte[] header, long index, bool last)
    {
        var ad = new byte[header.Length + 9];
        header.CopyTo(ad, 0);
        BinaryPrimitives.WriteInt64BigEndian(ad.AsSpan(header.Length), index);
        ad[^1] = last ? (byte)1 : (byte)0;
        return ad;
    }

    private sealed class EncryptingStream(Stream output, byte[] key, byte[] header, int blockSize) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _buffer = new byte[blockSize];
        private readonly byte[] _cipher = new byte[blockSize + TagSize];
        private int _filled;
        private long _index;
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
                // Un blocco pieno parte solo quando arriva altro: fino ad allora potrebbe essere l'ultimo.
                if (_filled == _buffer.Length) WriteBlock(last: false);
                var n = Math.Min(data.Length, _buffer.Length - _filled);
                data[..n].CopyTo(_buffer.AsSpan(_filled));
                _filled += n;
                data = data[n..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void WriteBlock(bool last)
        {
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _index);
            _aes.Encrypt(nonce, _buffer.AsSpan(0, _filled), _cipher.AsSpan(0, _filled), _cipher.AsSpan(_filled, TagSize), AssociatedData(header, _index, last));
            output.Write(_cipher, 0, _filled + TagSize);
            _index++;
            _filled = 0;
        }

        public override void Flush() => output.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                WriteBlock(last: true);
                output.Flush();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(_buffer);
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream(Stream input, byte[] key, byte[] header, int blockSize) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _cipher = new byte[blockSize + TagSize + 1];
        private readonly byte[] _plain = new byte[blockSize];
        private int _pending; // byte già letti in _cipher per il blocco successivo (la sbirciata)
        private int _plainOffset, _plainLength;
        private long _index;
        private bool _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (_plainOffset == _plainLength)
            {
                if (_done) return 0;
                NextBlock();
                if (_plainLength == 0) return 0;
            }
            var n = Math.Min(destination.Length, _plainLength - _plainOffset);
            _plain.AsSpan(_plainOffset, n).CopyTo(destination);
            _plainOffset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));

        private void NextBlock()
        {
            // Si legge un blocco intero più un byte: se il byte c'è, questo non è l'ultimo.
            var full = blockSize + TagSize;
            _pending += input.ReadAtLeast(_cipher.AsSpan(_pending), full + 1 - _pending, throwOnEndOfStream: false);
            var last = _pending <= full;
            var length = Math.Min(_pending, full);
            if (length < TagSize) throw new CryptographicException("Il backup è troncato.");

            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _index);
            var plainLength = length - TagSize;
            _aes.Decrypt(nonce, _cipher.AsSpan(0, plainLength), _cipher.AsSpan(plainLength, TagSize), _plain.AsSpan(0, plainLength),
                AssociatedData(header, _index, last));
            _index++;
            _plainOffset = 0;
            _plainLength = plainLength;

            if (last)
            {
                _done = true;
                _pending = 0;
            }
            else
            {
                _cipher[0] = _cipher[full];
                _pending = 1;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(_plain);
                input.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
