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
public class RecurringPostScheduler(FlarelyticsDbContext db, SocialMediaStorage storage, ILogger<RecurringPostScheduler> log)
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
        db.Add(post);
        foreach (var account in accounts)
        {
            var target = SocialPostTarget.For(post, account);
            post.AddTarget(target);
            db.Add(target);
        }

        // Una copia per uscita: il post resta com'era anche se poi la serie
        // cambia immagini o viene cancellata. Se il file non c'è più la riga si
        // crea lo stesso, e la pubblicazione dirà che manca.
        foreach (var source in recurring.Media.OrderBy(m => m.Position))
        {
            var copy = source.CopyFor(post.Id, source.Position);
            storage.Copy(source, copy);
            post.AddMedia(copy);
            db.Add(copy);
        }
        return true;
    }
}
