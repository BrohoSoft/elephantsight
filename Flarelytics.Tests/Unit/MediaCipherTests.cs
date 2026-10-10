using System.Security.Cryptography;
using Flarelytics.Core.Secrets;
using Flarelytics.Core.Social.Media;
using Microsoft.Extensions.Options;

namespace Flarelytics.Tests.Unit;

public class MediaCipherTests : IDisposable
{
    private const int Block = 4096;
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "media-cipher-" + Guid.NewGuid().ToString("N"));
    private readonly MediaCipher _cipher;
    private readonly MediaKey _key = new(Guid.NewGuid(), Guid.NewGuid(), MediaVariant.Original);

    public MediaCipherTests()
    {
        KeyRing.CreateKeyFile(_keys, "v1");
        _cipher = new MediaCipher(new KeyRing(Options.Create(new SecretsOptions { KeysDirectory = _keys, ActiveKeyVersion = "v1" })));
    }

    public void Dispose()
    {
        Directory.Delete(_keys, recursive: true);
        GC.SuppressFinalize(this);
    }

    private byte[] Encrypt(byte[] plain, MediaKey? key = null)
    {
        using var encrypted = _cipher.Encrypt(new MemoryStream(plain), plain.Length, key ?? _key, Block);
        using var output = new MemoryStream();
        encrypted.CopyTo(output);
        Assert.Equal(MediaCipher.EncryptedLength(plain.Length, Block), output.Length);
        return output.ToArray();
    }

    /// <summary>Come fa lo storage remoto: intestazione, poi i blocchi che servono, decifrati in streaming.</summary>
    private byte[] Decrypt(byte[] file, long from = 0, long? to = null, MediaKey? key = null)
    {
        var header = _cipher.ReadHeader(file.AsSpan(0, MediaCipher.HeaderSize), key ?? _key);
        var last = to ?? header.Length - 1;
        var first = header.Length == 0 ? 0 : from / header.BlockSize;
        var source = new MemoryStream(file, (int)header.EncryptedOffset(first), file.Length - (int)header.EncryptedOffset(first));
        using var plain = MediaCipher.Decrypt(source, header, key ?? _key, from, last);
        using var output = new MemoryStream();
        plain.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Random(int size) => RandomNumberGenerator.GetBytes(size);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Block)]
    [InlineData(Block * 3 + 17)]
    public void Cifra_e_decifra_a_blocchi_qualunque_lunghezza(int size)
    {
        var plain = Random(size);
        var file = Encrypt(plain);
        Assert.Equal(plain, Decrypt(file));
    }

    [Fact]
    public void Il_contenuto_in_chiaro_non_compare_nel_file_cifrato()
    {
        var plain = Enumerable.Repeat((byte)0xAB, Block * 2).ToArray();
        var file = Encrypt(plain);
        Assert.False(file.AsSpan().IndexOf(plain.AsSpan(0, 64)) >= 0);
    }

    [Fact]
    public void Un_intervallo_si_legge_decifrando_solo_i_suoi_blocchi()
    {
        var plain = Random(Block * 5 + 100);
        var file = Encrypt(plain);
        Assert.Equal(plain[(Block * 2 + 10)..(Block * 3 + 20)], Decrypt(file, Block * 2 + 10, Block * 3 + 19));
        Assert.Equal(plain[^50..], Decrypt(file, plain.Length - 50, plain.Length - 1));
    }

    [Fact]
    public void Un_file_spostato_su_un_altro_media_o_tenant_non_si_decifra()
    {
        var file = Encrypt(Random(Block * 2));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file, key: _key with { MediaId = Guid.NewGuid() }));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file, key: _key with { TenantId = Guid.NewGuid() }));
        // E l'originale non si fa passare per la miniatura.
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file, key: _key with { Variant = MediaVariant.Thumbnail }));
    }

    [Fact]
    public void Blocchi_scambiati_si_rifiutano()
    {
        var file = Encrypt(Random(Block * 3));
        var blockLength = Block + MediaCipher.TagSize;
        var swapped = (byte[])file.Clone();
        Array.Copy(file, MediaCipher.HeaderSize, swapped, MediaCipher.HeaderSize + blockLength, blockLength);
        Array.Copy(file, MediaCipher.HeaderSize + blockLength, swapped, MediaCipher.HeaderSize, blockLength);
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(swapped));
    }

    [Fact]
    public void Un_file_troncato_si_rifiuta_anche_cambiando_la_lunghezza()
    {
        var plain = Random(Block * 3);
        var file = Encrypt(plain);
        var withoutLast = file[..^(Block + MediaCipher.TagSize)];
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(withoutLast));

        // Dire nell'intestazione che il file è più corto non basta: la lunghezza è nei dati associati di ogni blocco.
        var shorter = (byte[])withoutLast.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(shorter.AsSpan(MediaCipher.HeaderSize - 8, 8), Block * 2);
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(shorter));
    }

    [Fact]
    public void Un_byte_alterato_si_riconosce()
    {
        var file = Encrypt(Random(Block * 2));
        file[MediaCipher.HeaderSize + 100] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file));
    }
}
