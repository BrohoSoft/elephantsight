using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Secrets;

namespace Flarelytics.Core.Social.Media;

/// <summary>Quale file di un media: l'originale o la miniatura per il calendario.</summary>
public enum MediaVariant
{
    Original = 0,
    Thumbnail = 1
}

/// <summary>Chi è un file: tenant, media e variante. Entra nei dati associati della cifratura.</summary>
public readonly record struct MediaKey(Guid TenantId, Guid MediaId, MediaVariant Variant)
{
    public override string ToString() => $"{TenantId:N}|{MediaId:N}|{(Variant == MediaVariant.Thumbnail ? "thumb" : "orig")}";
}

/// <summary>Quello che serve per decifrare: la DEK, la dimensione dei blocchi e la lunghezza in chiaro.</summary>
public sealed class MediaHeader(byte[] dek, int blockSize, long length) : IDisposable
{
    public byte[] Dek { get; } = dek;
    public int BlockSize { get; } = blockSize;
    public long Length { get; } = length;

    public long BlockCount => MediaCipher.BlockCount(Length, BlockSize);

    /// <summary>Dove inizia il blocco <paramref name="index"/> nel file cifrato.</summary>
    public long EncryptedOffset(long index) => MediaCipher.HeaderSize + index * (BlockSize + MediaCipher.TagSize);

    /// <summary>Quanti byte in chiaro ha il blocco (l'ultimo ha il resto).</summary>
    public int PlainLength(long index) =>
        index < BlockCount - 1 ? BlockSize : (int)(Length - (BlockCount - 1) * BlockSize);

    public void Dispose() => CryptographicOperations.ZeroMemory(Dek);
}

/// <summary>
/// La cifratura dei media sullo storage remoto: chi ospita i file (Bunny) vede
/// solo byte cifrati, le chiavi restano sul server.
/// </summary>
/// <remarks>
/// <para><b>A busta, come <see cref="SecretVault"/>.</b> Ogni file ha una sua
/// chiave dati (DEK) casuale, cifrata con la chiave master versionata.</para>
///
/// <para><b>A blocchi.</b> Il contenuto è diviso in blocchi (1 MB), ognuno
/// cifrato con AES-256-GCM e il suo tag. Così si decifra in streaming, senza
/// tenere in memoria un video da centinaia di MB, e una richiesta
/// <c>Range</c> scarica e decifra solo i blocchi che le servono. Il nonce è
/// l'indice del blocco: la DEK è diversa per ogni file, quindi non si ripete.</para>
///
/// <para><b>Legato al suo posto.</b> Nei dati associati entrano tenant, media
/// e variante (anche per la DEK), e per ogni blocco l'indice, se è l'ultimo,
/// la dimensione dei blocchi e la lunghezza totale. Un file spostato su un
/// altro media o tenant non si decifra; blocchi scambiati o tolti, o una
/// lunghezza cambiata nell'intestazione, fanno fallire il tag.</para>
///
/// <para>Formato:</para>
/// <code>
/// "ESMB" | formato (1) | versione KEK (16, utf-8, zeri in fondo)
/// nonce DEK (12) | DEK cifrata (32) | tag DEK (16)
/// dimensione blocco (4, big endian) | lunghezza in chiaro (8, big endian)
/// blocco 0: dati cifrati | tag (16)    …    ultimo blocco: il resto | tag (16)
/// </code>
/// </remarks>
public sealed class MediaCipher(KeyRing keys)
{
    private static readonly byte[] Magic = "ESMB"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int VersionField = 16;
    private const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HeaderSize = 4 + 1 + VersionField + NonceSize + KeyRing.KeySize + TagSize + 4 + 8;
    public const int DefaultBlockSize = 1024 * 1024;

    /// <summary>Quanti blocchi ha un file: almeno uno, anche vuoto (porta il segnale di "ultimo").</summary>
    public static long BlockCount(long length, int blockSize) => length == 0 ? 1 : (length + blockSize - 1) / blockSize;

    /// <summary>La lunghezza del file cifrato: serve per il Content-Length del caricamento.</summary>
    public static long EncryptedLength(long length, int blockSize = DefaultBlockSize) =>
        HeaderSize + BlockCount(length, blockSize) * TagSize + length;

    /// <summary>
    /// Un flusso che legge <paramref name="plaintext"/> (esattamente
    /// <paramref name="length"/> byte) e produce il file cifrato, blocco per
    /// blocco: niente resta in memoria oltre al blocco corrente.
    /// </summary>
    public Stream Encrypt(Stream plaintext, long length, MediaKey key, int blockSize = DefaultBlockSize) =>
        new EncryptingStream(this, plaintext, length, key, blockSize);

    /// <summary>Legge l'intestazione e apre la DEK. Fallisce se il file non è di questo media o è stato alterato.</summary>
    public MediaHeader ReadHeader(ReadOnlySpan<byte> header, MediaKey key)
    {
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic) || header[4] != FormatVersion)
            throw new CryptographicException("Non è un media cifrato di ElephantSight.");

        var version = Encoding.UTF8.GetString(header.Slice(5, VersionField)).TrimEnd('\0');
        var p = 5 + VersionField;
        var nonce = header.Slice(p, NonceSize);
        var wrapped = header.Slice(p + NonceSize, KeyRing.KeySize);
        var tag = header.Slice(p + NonceSize + KeyRing.KeySize, TagSize);
        p += NonceSize + KeyRing.KeySize + TagSize;
        var blockSize = BinaryPrimitives.ReadInt32BigEndian(header.Slice(p, 4));
        var length = BinaryPrimitives.ReadInt64BigEndian(header.Slice(p + 4, 8));
        if (blockSize is < 1024 or > 64 * 1024 * 1024 || length < 0) throw new CryptographicException("Intestazione del media non valida.");

        var dek = new byte[KeyRing.KeySize];
        using (var aes = new AesGcm(keys.Get(version), TagSize))
        {
            aes.Decrypt(nonce, wrapped, tag, dek, DekAad(key));
        }
        return new MediaHeader(dek, blockSize, length);
    }

    /// <summary>
    /// Decifra i blocchi da <paramref name="firstBlock"/> in poi, leggendo da
    /// <paramref name="encrypted"/> (già all'inizio di quel blocco), e restituisce
    /// solo i byte in chiaro fra <paramref name="from"/> e <paramref name="to"/> (compresi).
    /// </summary>
    public static Stream Decrypt(Stream encrypted, MediaHeader header, MediaKey key, long from, long to) =>
        new DecryptingStream(encrypted, header, key, from, to);

    private byte[] BuildHeader(byte[] dek, MediaKey key, int blockSize, long length)
    {
        var version = keys.ActiveVersion;
        var versionBytes = Encoding.UTF8.GetBytes(version);
        if (versionBytes.Length > VersionField) throw new InvalidOperationException("Il nome della chiave master è troppo lungo per i media cifrati.");

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = FormatVersion;
        versionBytes.CopyTo(header, 5);
        var p = 5 + VersionField;
        var nonce = header.AsSpan(p, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(keys.Get(version), TagSize))
        {
            aes.Encrypt(nonce, dek, header.AsSpan(p + NonceSize, KeyRing.KeySize), header.AsSpan(p + NonceSize + KeyRing.KeySize, TagSize), DekAad(key));
        }
        p += NonceSize + KeyRing.KeySize + TagSize;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(p, 4), blockSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(p + 4, 8), length);
        return header;
    }

    private static byte[] DekAad(MediaKey key) => Encoding.ASCII.GetBytes($"social-media-dek|{key}");

    internal static byte[] BlockAad(MediaKey key, int blockSize, long length, long index, bool last) =>
        Encoding.ASCII.GetBytes($"social-media|{key}|{blockSize}|{length}|{index}|{(last ? 1 : 0)}");

    internal static void Nonce(Span<byte> nonce, long index)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
    }

    /// <summary>Produce l'intestazione e poi un blocco cifrato alla volta, leggendo dal file in chiaro solo quando serve.</summary>
    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly long _length;
        private readonly MediaKey _key;
        private readonly int _blockSize;
        private readonly long _blockCount;
        private readonly AesGcm _aes;
        private readonly byte[] _dek;
        private readonly byte[] _plain;
        private byte[] _pending;
        private int _pendingOffset;
        private long _nextBlock;
        private long _position;

        public EncryptingStream(MediaCipher cipher, Stream source, long length, MediaKey key, int blockSize)
        {
            _source = source;
            _length = length;
            _key = key;
            _blockSize = blockSize;
            _blockCount = BlockCount(length, blockSize);
            _dek = RandomNumberGenerator.GetBytes(KeyRing.KeySize);
            _aes = new AesGcm(_dek, TagSize);
            _plain = new byte[blockSize];
            _pending = cipher.BuildHeader(_dek, key, blockSize, length);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => EncryptedLength(_length, _blockSize);
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pendingOffset == _pending.Length)
            {
                if (_nextBlock == _blockCount) return 0;
                await NextBlockAsync(ct);
            }
            var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, n).CopyTo(buffer);
            _pendingOffset += n;
            _position += n;
            return n;
        }

        private async Task NextBlockAsync(CancellationToken ct)
        {
            var index = _nextBlock++;
            var last = index == _blockCount - 1;
            var size = last ? (int)(_length - index * _blockSize) : _blockSize;
            var read = await _source.ReadAtLeastAsync(_plain.AsMemory(0, size), size, throwOnEndOfStream: false, ct);
            if (read != size) throw new InvalidOperationException("Il file in chiaro è più corto della lunghezza dichiarata.");

            var block = new byte[size + TagSize];
            Span<byte> nonce = stackalloc byte[NonceSize];
            Nonce(nonce, index);
            _aes.Encrypt(nonce, _plain.AsSpan(0, size), block.AsSpan(0, size), block.AsSpan(size, TagSize), BlockAad(_key, _blockSize, _length, index, last));
            CryptographicOperations.ZeroMemory(_plain.AsSpan(0, size));
            _pending = block;
            _pendingOffset = 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_dek);
                CryptographicOperations.ZeroMemory(_plain);
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Legge un blocco cifrato alla volta, lo verifica e restituisce la parte in chiaro richiesta.</summary>
    private sealed class DecryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly MediaHeader _header;
        private readonly MediaKey _key;
        private readonly AesGcm _aes;
        private readonly long _to;
        private long _position; // posizione in chiaro (assoluta) del prossimo byte da restituire
        private long _nextBlock;
        private byte[] _plain = [];
        private int _plainOffset;

        public DecryptingStream(Stream source, MediaHeader header, MediaKey key, long from, long to)
        {
            _source = source;
            _header = header;
            _key = key;
            _aes = new AesGcm(header.Dek, TagSize);
            _position = from;
            _to = to;
            _nextBlock = header.Length == 0 ? 0 : from / header.BlockSize;
            Length = to - from + 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position > _to) return 0;
            if (_plainOffset == _plain.Length) await NextBlockAsync(ct);

            var n = (int)Math.Min(Math.Min(buffer.Length, _plain.Length - _plainOffset), _to - _position + 1);
            _plain.AsMemory(_plainOffset, n).CopyTo(buffer);
            _plainOffset += n;
            _position += n;
            return n;
        }

        private async Task NextBlockAsync(CancellationToken ct)
        {
            var index = _nextBlock++;
            if (index >= _header.BlockCount) throw new CryptographicException("Il media cifrato finisce prima del previsto.");
            var size = _header.PlainLength(index);
            var block = new byte[size + TagSize];
            var read = await _source.ReadAtLeastAsync(block, block.Length, throwOnEndOfStream: false, ct);
            // Un file troncato: l'ultimo blocco manca, o è a metà.
            if (read != block.Length) throw new CryptographicException("Il media cifrato è incompleto.");

            var plain = new byte[size];
            Span<byte> nonce = stackalloc byte[NonceSize];
            Nonce(nonce, index);
            _aes.Decrypt(nonce, block.AsSpan(0, size), block.AsSpan(size, TagSize), plain,
                BlockAad(_key, _header.BlockSize, _header.Length, index, index == _header.BlockCount - 1));

            // Del primo blocco si salta la parte prima di "from".
            var skip = (int)(_position - index * (long)_header.BlockSize);
            _plain = plain;
            _plainOffset = Math.Max(0, skip);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                _header.Dispose();
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
