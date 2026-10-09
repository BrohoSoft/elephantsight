using Flarelytics.Api.Auth;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Email;

/// <summary>I testi delle email dell'account, con i link verso il frontend.</summary>
public class AccountEmails(IEmailSender sender, IOptions<AuthOptions> options)
{
    /// <summary>False se l'installazione non ha SMTP: le email non partono, e il pannello lo deve sapere.</summary>
    public bool Enabled => sender is not LogEmailSender;

    private string AppUrl => options.Value.PublicAppUrl.TrimEnd('/');

    public Task SendPasswordResetAsync(string to, string fullName, string token, CancellationToken ct) =>
        sender.SendAsync(new EmailMessage(to, "Reimposta la password",
            $"""
            Ciao {fullName},

            per scegliere una nuova password apri questo link:
            {AppUrl}/reset-password?token={Uri.EscapeDataString(token)}

            Il link vale un'ora. Se non l'hai chiesto tu, ignora questa email: la password resta quella di prima.
            """), ct);

    /// <returns>Se l'email è partita davvero.</returns>
    public async Task<bool> SendInvitationAsync(string to, string organization, string invitedBy, string token, CancellationToken ct)
    {
        await sender.SendAsync(new EmailMessage(to, $"{invitedBy} ti ha invitato su WatchStore",
            $"""
            Ciao,

            {invitedBy} ti ha invitato a entrare in "{organization}" su WatchStore.
            Per accettare apri questo link:
            {AppUrl}/accept-invite?token={Uri.EscapeDataString(token)}

            L'invito vale 7 giorni. Se non te l'aspettavi, ignora questa email.
            """), ct);
        return Enabled;
    }
}
