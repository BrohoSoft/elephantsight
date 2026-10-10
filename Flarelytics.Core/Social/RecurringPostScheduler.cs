using Flarelytics.Core.Database;
using Flarelytics.Core.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flarelytics.Core.Social;

/// <summary>
/// Le uscite dei post ricorrenti: quando arriva l'ora, il post ricorrente
/// diventa un <see cref="SocialPost"/> normale, con i suoi account e una copia
/// dei suoi file, che il worker pubblica subito dopo.
/// </summary>
public class RecurringPostScheduler(FlarelyticsDbContext db, Media.SocialMediaStore storage, ILogger<RecurringPostScheduler> log)
{
    /// <summary>
    /// Un'uscita in ritardo di più di così (il server era spento) si salta:
    /// dopo un fermo di tre giorni non devono uscire tre post giornalieri insieme.
    /// </summary>
    public static readonly TimeSpan MissedAfter = TimeSpan.FromHours(1);

    public async Task CreateDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = await db.Set<SocialRecurringPost>().Include(r => r.Media)
            .Where(r => !r.IsPaused && r.NextOccurrenceUtc != null && r.NextOccurrenceUtc <= nowUtc)
            .ToListAsync(ct);

        foreach (var recurring in due)
        {
            var at = recurring.NextOccurrenceUtc!.Value;
            var created = false;
            if (nowUtc - at > MissedAfter)
            {
                log.LogWarning("Uscita del {At:u} del post ricorrente {Id} saltata: il server non ha girato in tempo", at, recurring.Id);
            }
            else
            {
                created = await CreatePostAsync(recurring, at, ct);
                if (created) log.LogInformation("Creata l'uscita del {At:u} del post ricorrente {Id}: parte adesso", at, recurring.Id);
            }

            recurring.Advance(at, created, nowUtc);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> CreatePostAsync(SocialRecurringPost recurring, DateTime at, CancellationToken ct)
    {
        var accounts = await db.Set<SocialAccount>().Where(a => recurring.AccountIds.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count == 0)
        {
            log.LogWarning("Post ricorrente {Id} senza account: uscita del {At:u} saltata", recurring.Id, at);
            return false;
        }

        var post = SocialPost.FromRecurring(recurring, at);

        // Una copia per uscita: il post resta com'era anche se poi la serie
        // cambia immagini o viene cancellata, e le copie si cancellano dopo la
        // pubblicazione come quelle di ogni post (vedi CleanupPublishedMediaAsync).
        // Se la copia non riesce (file perso, storage che non risponde),
        // l'uscita si salta invece di partire senza le sue immagini.
        var copies = new List<SocialMedia>();
        try
        {
            foreach (var source in recurring.Media.OrderBy(m => m.Position))
            {
                var copy = source.CopyFor(post.Id, source.Position);
                await storage.CopyAsync(source, copy, ct);
                copies.Add(copy);
            }
        }
        catch (Exception e) when (e is Media.MediaStorageUnavailableException or FileNotFoundException)
        {
            foreach (var c in copies) await storage.DeleteAsync(c, ct);
            log.LogError("Uscita del {At:u} del post ricorrente {Id} saltata: non si riescono a copiare le immagini ({Message})", at, recurring.Id, e.Message);
            return false;
        }

        db.Add(post);
        foreach (var account in accounts)
        {
            var target = SocialPostTarget.For(post, account);
            post.AddTarget(target);
            db.Add(target);
        }
        foreach (var copy in copies)
        {
            post.AddMedia(copy);
            db.Add(copy);
        }
        return true;
    }
}
