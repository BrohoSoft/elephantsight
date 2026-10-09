using System.ComponentModel.DataAnnotations;
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
/// In sviluppo le email non partono: si leggono nel log, link compresi.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        log.LogInformation("Email per {To}: {Subject}\n{Body}", message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }
}

/// <summary>SMTP. Sezione <c>Email:Smtp</c>.</summary>
public class SmtpOptions
{
    public const string Section = "Email:Smtp";

    [Required] public string Host { get; set; } = null!;
    public int Port { get; set; } = 587;
    [Required] public string Username { get; set; } = null!;
    [Required] public string Password { get; set; } = null!;
    [Required, EmailAddress] public string FromAddress { get; set; } = null!;
    public string FromName { get; set; } = "ElephantSight";
}

/// <summary>
/// Le email vere, via SMTP con MailKit. Nessuna libreria del provider: cambiarlo
/// vuol dire cambiare la configurazione, non il codice.
/// </summary>
public class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(o.Username, o.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
