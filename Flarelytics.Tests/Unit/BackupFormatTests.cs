using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Backups;

namespace Flarelytics.Tests.Unit;

public class BackupFormatTests
{
    private const string Password = "una-password-lunga";
    private const int Iterations = 1000; // nei test basta: la sicurezza della derivazione non è quello che si prova qui
    private const int Block = 4096;

    private static byte[] Encrypt(byte[] plain, string password = Password)
    {
        using var output = new MemoryStream();
        using (var encrypted = BackupCipher.Encrypt(output, password, Iterations, Block))
        {
            // A pezzi di grandezze diverse, come arrivano da pg_dump.
            for (var i = 0; i < plain.Length; i += 1000) encrypted.Write(plain, i, Math.Min(1000, plain.Length - i));
        }
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] file, string password = Password)
    {
        using var plain = BackupCipher.Decrypt(new MemoryStream(file), password);
        using var output = new MemoryStream();
        plain.CopyTo(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Block)]
    [InlineData(Block * 3 + 17)]
    public void Cifra_e_decifra_qualunque_lunghezza(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var file = Encrypt(plain);
        Assert.Equal(plain, Decrypt(file));
        Assert.True(size < 64 || file.AsSpan().IndexOf(plain.AsSpan(0, 64)) < 0);
    }

    [Fact]
    public void Con_la_password_sbagliata_non_si_apre()
    {
        var file = Encrypt(RandomNumberGenerator.GetBytes(Block * 2));
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file, "un'altra-password"));
    }

    [Fact]
    public void Un_file_troncato_o_alterato_si_rifiuta()
    {
        var file = Encrypt(RandomNumberGenerator.GetBytes(Block * 3));
        // Tolto l'ultimo blocco: quello prima non era segnato come ultimo.
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file[..^(Block + BackupCipher.TagSize)]));
        // Tagliato a metà di un blocco.
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(file[..^100]));
        var altered = (byte[])file.Clone();
        altered[BackupCipher.HeaderSize + 10] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(altered));
        // Il sale cambiato cambia la chiave.
        var salt = (byte[])file.Clone();
        salt[12] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Decrypt(salt));
    }

    [Fact]
    public void Due_backup_dello_stesso_contenuto_sono_diversi()
    {
        var plain = RandomNumberGenerator.GetBytes(500);
        Assert.NotEqual(Encrypt(plain), Encrypt(plain));
    }

    [Fact]
    public async Task L_archivio_restituisce_i_file_nell_ordine_e_senza_la_fine_si_rifiuta()
    {
        var big = RandomNumberGenerator.GetBytes(700_000);
        using var output = new MemoryStream();
        var writer = new BackupArchiveWriter(output);
        await writer.AddAsync("keys/v1.key", new MemoryStream("chiave"u8.ToArray()), CancellationToken.None);
        await writer.AddAsync("vuoto", new MemoryStream(), CancellationToken.None);
        await writer.AddAsync("database.dump", new MemoryStream(big), CancellationToken.None);
        writer.Complete();

        var read = new List<(string, byte[])>();
        await BackupArchiveReader.ReadAsync(new MemoryStream(output.ToArray()), async (name, content) =>
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            read.Add((name, copy.ToArray()));
        }, CancellationToken.None);
        Assert.Equal(["keys/v1.key", "vuoto", "database.dump"], read.Select(r => r.Item1));
        Assert.Equal("chiave", Encoding.UTF8.GetString(read[0].Item2));
        Assert.Empty(read[1].Item2);
        Assert.Equal(big, read[2].Item2);

        // Anche leggendo solo i nomi (il resto si salta).
        var names = new List<string>();
        await BackupArchiveReader.ReadAsync(new MemoryStream(output.ToArray()), (name, _) => { names.Add(name); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(3, names.Count);

        await Assert.ThrowsAnyAsync<Exception>(() => BackupArchiveReader.ReadAsync(new MemoryStream(output.ToArray()[..^1]), (_, _) => Task.CompletedTask, CancellationToken.None));
    }

    private static BackupOptions Rule(string time = "03:00", int every = 1, string zone = "Europe/Rome") =>
        new() { Time = time, EveryDays = every, TimeZone = zone };

    [Fact]
    public void L_appuntamento_segue_l_ora_locale_anche_con_l_ora_legale()
    {
        // 10 ottobre 2026, ora legale (UTC+2): le 03:00 di Roma sono l'01:00 UTC.
        Assert.Equal(new DateTime(2026, 10, 10, 1, 0, 0, DateTimeKind.Utc), BackupSchedule.LatestSlotUtc(Rule(), new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc)));
        // Prima delle 03:00: vale quello di ieri.
        Assert.Equal(new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc), BackupSchedule.LatestSlotUtc(Rule(), new DateTime(2026, 10, 10, 0, 30, 0, DateTimeKind.Utc)));
        // A dicembre, ora solare (UTC+1).
        Assert.Equal(new DateTime(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc), BackupSchedule.LatestSlotUtc(Rule(), new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 12, 2, 2, 0, 0, DateTimeKind.Utc), BackupSchedule.NextSlotUtc(Rule(), new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Ogni_tre_giorni_i_giorni_restano_quelli_anche_se_si_salta_un_giro()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var latest = BackupSchedule.LatestSlotUtc(Rule(every: 3), now)!.Value;
        var next = BackupSchedule.NextSlotUtc(Rule(every: 3), now)!.Value;
        Assert.Equal(TimeSpan.FromDays(3), next - latest);
        Assert.Equal(latest, BackupSchedule.LatestSlotUtc(Rule(every: 3), latest.AddDays(2)));
        Assert.Null(BackupSchedule.LatestSlotUtc(Rule(time: "25:00"), now));
        Assert.Null(BackupSchedule.LatestSlotUtc(Rule(zone: "Marte/Olympus"), now));
    }
}
