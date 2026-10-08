using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Secrets;
using Microsoft.Extensions.Options;

namespace Flarelytics.Tests.Unit;

public class SecretVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flarelytics-vault", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _credential = Guid.NewGuid();
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("-----BEGIN PRIVATE KEY-----\nsegreto\n-----END PRIVATE KEY-----");

    private string Keys => Path.Combine(_root, "keys");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SecretVault Vault(string active)
    {
        KeyRing.CreateKeyFile(Keys, active);
        var options = Options.Create(new SecretsOptions
        {
            KeysDirectory = Keys,
            ActiveKeyVersion = active,
            StorageDirectory = Path.Combine(_root, "data")
        });
        return new SecretVault(new KeyRing(options), options);
    }

    [Fact]
    public async Task Rilegge_quello_che_ha_scritto()
    {
        var vault = Vault("v1");
        Assert.Equal("v1", await vault.WriteAsync(_tenant, _credential, Secret, default));
        Assert.Equal(Secret, await vault.ReadAsync(_tenant, _credential, default));
    }

    [Fact]
    public async Task Il_file_non_contiene_il_segreto_in_chiaro()
    {
        var vault = Vault("v1");
        await vault.WriteAsync(_tenant, _credential, Secret, default);

        var file = await File.ReadAllBytesAsync(vault.PathFor(_tenant, _credential));
        Assert.DoesNotContain("segreto", Encoding.UTF8.GetString(file));
    }

    [Fact]
    public async Task Il_file_e_leggibile_solo_dal_proprietario()
    {
        if (OperatingSystem.IsWindows()) return;

        var vault = Vault("v1");
        await vault.WriteAsync(_tenant, _credential, Secret, default);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(vault.PathFor(_tenant, _credential)));
    }

    /// <summary>
    /// Chi può scrivere sul disco non deve poter far usare a un tenant la
    /// chiave di un altro spostando i file.
    /// </summary>
    [Fact]
    public async Task Un_file_spostato_sotto_un_altro_tenant_non_si_decifra()
    {
        var vault = Vault("v1");
        await vault.WriteAsync(_tenant, _credential, Secret, default);

        var other = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(vault.PathFor(other, _credential))!);
        File.Copy(vault.PathFor(_tenant, _credential), vault.PathFor(other, _credential));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.ReadAsync(other, _credential, default));
    }

    [Fact]
    public async Task Un_byte_alterato_si_riconosce()
    {
        var vault = Vault("v1");
        await vault.WriteAsync(_tenant, _credential, Secret, default);

        var path = vault.PathFor(_tenant, _credential);
        var file = await File.ReadAllBytesAsync(path);
        file[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, file);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => vault.ReadAsync(_tenant, _credential, default));
    }

    [Fact]
    public async Task Dopo_la_rotazione_la_chiave_vecchia_non_serve_piu()
    {
        await Vault("v1").WriteAsync(_tenant, _credential, Secret, default);

        // Si aggiunge v2, la si fa attiva, si ricifra.
        var rotated = Vault("v2");
        Assert.Equal("v2", await rotated.RewrapAsync(_tenant, _credential, default));

        // Si toglie v1: il file si legge lo stesso.
        File.Delete(Path.Combine(Keys, "v1.key"));
        Assert.Equal(Secret, await Vault("v2").ReadAsync(_tenant, _credential, default));
    }

    [Fact]
    public void Senza_la_chiave_attiva_non_parte()
    {
        Directory.CreateDirectory(Keys);
        var options = Options.Create(new SecretsOptions { KeysDirectory = Keys, ActiveKeyVersion = "v9", StorageDirectory = _root });

        Assert.Throws<InvalidOperationException>(() => new KeyRing(options));
    }
}
