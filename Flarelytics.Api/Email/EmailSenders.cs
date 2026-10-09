using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Flarelytics.Api.Email;

public record EmailMessage(string To, string Subject, string Body);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// L'SMTP (sezione <c>Email:Smtp</c>): dal <c>.env</c> o dalle impostazioni
/// dell'istanza, che hanno la precedenza e cambiano senza riavviare.
/// </summary>
public class SmtpOptions
{
    public const string Section = "Email:Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>Vuoto: il server non chiede di autenticarsi.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "ElephantSight";

    /// <summary>Basta un server e un mittente: senza, le email non partono.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// Manda le email con l'SMTP configurato in quel momento; senza SMTP le scrive
/// solo nel log (in sviluppo i link si leggono lì). Legge la configurazione a
/// ogni invio, così un SMTP inserito dalle impostazioni vale subito.
/// </summary>
public class ConfiguredEmailSender(IOptionsMonitor<SmtpOptions> options, ILogger<ConfiguredEmailSender> log) : IEmailSender
{
    public bool Enabled => options.CurrentValue.Enabled;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            log.LogInformation("Email per {To} non mandata (SMTP non configurato): {Subject}\n{Body}", message.To, message.Subject, message.Body);
            return;
        }
        await SmtpEmail.SendAsync(o, message, ct);
    }
}

/// <summary>L'invio vero, con MailKit. Nessuna libreria del provider: cambiarlo vuol dire cambiare la configurazione.</summary>
public static class SmtpEmail
{
    public static async Task SendAsync(SmtpOptions o, EmailMessage message, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        // 465: TLS dalla connessione; le altre porte (587, 25) con STARTTLS obbligatorio.
        await client.ConnectAsync(o.Host, o.Port, o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrWhiteSpace(o.Username)) await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
