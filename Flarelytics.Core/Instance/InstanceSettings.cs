using System.Security.Cryptography;
using System.Text;
using Flarelytics.Core.Database;
using Flarelytics.Core.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;

namespace Flarelytics.Core.Instance;

/// <summary>
/// Un'impostazione dell'installazione inserita dal pannello (SMTP, app social),
/// sempre cifrata con le chiavi master: anche quelle non segrete, così la
/// tabella non dice niente a chi ha solo il database.
/// </summary>
/// <remarks>
/// Non è del tenant: vale per tutta l'installazione, e la modifica solo un
/// amministratore dell'istanza.
/// </remarks>
public class InstanceSetting
{
    public string Key { get; private set; } = null!;
    public string ProtectedValue { get; private set; } = null!;
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    private InstanceSetting() { }

    public static InstanceSetting Create(string key) => new() { Key = key };

    public void Set(string protectedValue, Guid? userId, DateTime nowUtc)
    {
        ProtectedValue = protectedValue;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Il contesto della cifratura: un valore copiato su un'altra chiave non si decifra.</summary>
    public static string ContextFor(string key) => $"instance-setting|{key}";
}

public class InstanceSettingConfiguration : IEntityTypeConfiguration<InstanceSetting>
{
    public void Configure(EntityTypeBuilder<InstanceSetting> b)
    {
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).HasMaxLength(100);
        b.Property(s => s.ProtectedValue).HasMaxLength(4000);
    }
}

/// <summary>Un campo delle impostazioni: dove finisce nella configurazione, e se è un segreto (si scrive e non si rilegge).</summary>
public sealed record InstanceSettingField(string Group, string Name, string ConfigPath, bool Secret)
{
    public string Key => $"{Group}.{Name}";
}

/// <summary>Tutte le impostazioni che si possono mettere dal pannello, divise per gruppo.</summary>
public static class InstanceSettingCatalog
{
    public static readonly IReadOnlyList<InstanceSettingField> Fields =
    [
        new("smtp", "host", "Email:Smtp:Host", false),
        new("smtp", "port", "Email:Smtp:Port", false),
        new("smtp", "username", "Email:Smtp:Username", false),
        new("smtp", "password", "Email:Smtp:Password", true),
        new("smtp", "fromAddress", "Email:Smtp:FromAddress", false),
        new("smtp", "fromName", "Email:Smtp:FromName", false),
        new("meta", "appId", "Social:Meta:AppId", false),
        new("meta", "appSecret", "Social:Meta:AppSecret", true),
        new("instagram", "appId", "Social:Instagram:AppId", false),
        new("instagram", "appSecret", "Social:Instagram:AppSecret", true),
        new("tiktok", "clientKey", "Social:TikTok:ClientKey", false),
        new("tiktok", "clientSecret", "Social:TikTok:ClientSecret", true),
        new("threads", "appId", "Social:Threads:AppId", false),
        new("threads", "appSecret", "Social:Threads:AppSecret", true),
        // Lo storage remoto dei file dei post. La modalità (MEDIA_STORAGE) no:
        // si impone dall'ambiente, così chi installa decide se il disco si può usare.
        new("bunny", "storageZone", "Media:Bunny:StorageZone", false),
        new("bunny", "region", "Media:Bunny:Region", false),
        new("bunny", "accessKey", "Media:Bunny:AccessKey", true),
        // I backup: ci sono solo se la funzione è accesa (Backups:Enabled, dall'ambiente).
        new("backup", "active", "Backups:Active", false),
        new("backup", "time", "Backups:Time", false),
        new("backup", "timeZone", "Backups:TimeZone", false),
        new("backup", "everyDays", "Backups:EveryDays", false),
        new("backup", "keep", "Backups:Keep", false),
        new("backup", "password", "Backups:Password", true),
    ];

    public static IEnumerable<InstanceSettingField> Group(string group) => Fields.Where(f => f.Group == group);

    public static bool IsGroup(string group) => Fields.Any(f => f.Group == group);

    /// <summary>
    /// I gruppi che chi installa può togliere dal pannello con una variabile
    /// d'ambiente: i backup (<c>BACKUPS_ENABLED=false</c>) e lo storage Bunny
    /// (<c>MEDIA_STORAGE_LOCKED=true</c>, vale solo il .env). Un gruppo non
    /// disponibile non si vede, non si salva, e i valori già salvati dal
    /// pannello non contano più.
    /// </summary>
    public static bool IsAvailable(string group, IConfiguration configuration) => group switch
    {
        "backup" => configuration.GetValue("Backups:Enabled", true),
        "bunny" => !configuration.GetValue("Media:Locked", false),
        _ => true
    };
}

/// <summary>
/// La sorgente di configurazione delle impostazioni dell'istanza: aggiunta per
/// ultima, vince sul .env. Parte vuota; la riempie <see cref="InstanceSettingsStore"/>
/// all'avvio e a ogni salvataggio, e il ricaricamento avvisa chi usa
/// <c>IOptionsMonitor</c>: il nuovo valore vale subito, senza riavvio.
/// </summary>
public sealed class InstanceSettingsConfigurationSource : IConfigurationSource
{
    public InstanceSettingsConfigurationProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class InstanceSettingsConfigurationProvider : ConfigurationProvider
{
    public void Apply(IDictionary<string, string?> values)
    {
        Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

/// <summary>Legge e scrive le impostazioni dell'istanza, cifrate, e le porta nella configurazione.</summary>
public class InstanceSettingsStore(FlarelyticsDbContext db, FieldProtector protector, InstanceSettingsConfigurationProvider provider, IConfiguration configuration)
{
    /// <summary>I valori in chiaro che il pannello ha impostato, per chiave (<c>smtp.host</c>).</summary>
    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        var values = new Dictionary<string, string>();
        foreach (var s in await db.Set<InstanceSetting>().AsNoTracking().ToListAsync(ct))
        {
            if (InstanceSettingCatalog.Fields.All(f => f.Key != s.Key)) continue;
            try
            {
                var bytes = protector.Unprotect(s.ProtectedValue, InstanceSetting.ContextFor(s.Key));
                values[s.Key] = Encoding.UTF8.GetString(bytes);
                CryptographicOperations.ZeroMemory(bytes);
            }
            catch (CryptographicException)
            {
                // Una chiave master cambiata senza rotazione: il valore si ignora e vale il .env.
            }
        }
        return values;
    }

    /// <summary>Porta i valori nella configurazione: da qui in poi le opzioni li vedono.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var values = await ReadAsync(ct);
        // Le chiavi dei gruppi non disponibili non vengono mai da qui (Backups:Enabled
        // e Media:Locked non sono nel catalogo): leggerle ora non vede il pannello.
        provider.Apply(InstanceSettingCatalog.Fields
            .Where(f => values.ContainsKey(f.Key) && InstanceSettingCatalog.IsAvailable(f.Group, configuration))
            .ToDictionary(f => f.ConfigPath, f => (string?)values[f.Key]));
    }

    /// <summary>
    /// Salva i campi di un gruppo. Un campo null resta com'era (per i segreti:
    /// non si rimanda quello che non si vede); una stringa vuota toglie il
    /// valore dal pannello, e torna a valere il .env.
    /// </summary>
    public async Task SaveAsync(string group, IReadOnlyDictionary<string, string?> changes, Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.Set<InstanceSetting>().ToListAsync(ct);
        foreach (var field in InstanceSettingCatalog.Group(group))
        {
            if (!changes.TryGetValue(field.Name, out var value) || value is null) continue;
            var row = existing.SingleOrDefault(s => s.Key == field.Key);
            if (value.Trim().Length == 0)
            {
                if (row is not null) db.Remove(row);
                continue;
            }
            if (row is null)
            {
                row = InstanceSetting.Create(field.Key);
                db.Add(row);
            }
            row.Set(protector.Protect(Encoding.UTF8.GetBytes(value.Trim()), InstanceSetting.ContextFor(field.Key)), userId, now);
        }
        await db.SaveChangesAsync(ct);
        await LoadAsync(ct);
    }

    /// <summary>Toglie tutto il gruppo dal pannello: torna a valere il .env.</summary>
    public async Task ClearAsync(string group, CancellationToken ct)
    {
        var keys = InstanceSettingCatalog.Group(group).Select(f => f.Key).ToList();
        db.RemoveRange(await db.Set<InstanceSetting>().Where(s => keys.Contains(s.Key)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        await LoadAsync(ct);
    }
}
