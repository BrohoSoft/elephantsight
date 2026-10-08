using System.Collections.Concurrent;
using Flarelytics.Api.Email;
using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Stores;

namespace Flarelytics.Tests.Integration.Infrastructure;

public class RecordingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public EmailMessage LastTo(string to) => Sent.Last(m => m.To == to.ToLowerInvariant());
}

/// <summary>
/// Uno store che risponde quello che gli si dice. Registra anche il segreto
/// ricevuto, per provare che arriva decifrato e intero.
/// </summary>
public class FakeStoreGateway(Store store) : IStoreGateway
{
    public Store Store => store;
    public VerificationResult NextResult { get; set; } = VerificationResult.Ok;
    public List<StoreAppInfo> Apps { get; } = [];
    public string? LastSecret { get; private set; }

    public Task<VerificationResult> VerifyAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        LastSecret = System.Text.Encoding.UTF8.GetString(secret.Span);
        return Task.FromResult(NextResult);
    }

    public Task<IReadOnlyList<StoreAppInfo>> ListAppsAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        LastSecret = System.Text.Encoding.UTF8.GetString(secret.Span);
        return Task.FromResult<IReadOnlyList<StoreAppInfo>>(Apps);
    }
}
