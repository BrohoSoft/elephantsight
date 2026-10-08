using Flarelytics.Api.Auth;
using Microsoft.Extensions.Options;

namespace Flarelytics.Api.Email;

/// <summary>I testi delle email dell'account, con i link verso il frontend.</summary>
public class AccountEmails(IEmailSender sender, IOptions<AuthOptions> options)
{
    private string AppUrl => options.Value.PublicAppUrl.TrimEnd('/');

    public Task SendConfirmationAsync(string to, string fullName, string token, CancellationToken ct) =>
        sender.SendAsync(new EmailMessage(to, "Conferma il tuo indirizzo email",
            $"""
            Ciao {fullName},

            per attivare il tuo account Flarelytics apri questo link:
            {AppUrl}/confirm-email?token={Uri.EscapeDataString(token)}

            Il link vale 24 ore. Se non ti sei registrato tu, ignora questa email.
            """), ct);

    public Task SendPasswordResetAsync(string to, string fullName, string token, CancellationToken ct) =>
        sender.SendAsync(new EmailMessage(to, "Reimposta la password",
            $"""
            Ciao {fullName},

            per scegliere una nuova password apri questo link:
            {AppUrl}/reset-password?token={Uri.EscapeDataString(token)}

            Il link vale un'ora. Se non l'hai chiesto tu, ignora questa email: la password resta quella di prima.
            """), ct);

    public Task SendInvitationAsync(string to, string organization, string invitedBy, string token, CancellationToken ct) =>
        sender.SendAsync(new EmailMessage(to, $"{invitedBy} ti ha invitato su Flarelytics",
            $"""
            Ciao,

            {invitedBy} ti ha invitato a entrare in "{organization}" su Flarelytics.
            Per accettare apri questo link:
            {AppUrl}/accept-invite?token={Uri.EscapeDataString(token)}

            L'invito vale 7 giorni. Se non te l'aspettavi, ignora questa email.
            """), ct);
}
