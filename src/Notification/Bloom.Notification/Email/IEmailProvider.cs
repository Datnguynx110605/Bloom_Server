namespace Bloom.Notification.Email;

/// <summary>Every email call Notification makes (HLD-ADR-018): Resend in the cloud, SMTP (Mailpit) locally. One try per call: the sender owns the retries.</summary>
public interface IEmailProvider
{
    /// <summary>Sends <paramref name="message"/> from <c>Email:From</c>. Returns the provider's message ID (null for SMTP). Failures surface as <see cref="EmailSendException"/>; the caller's own cancellation does not.</summary>
    Task<string?> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct);
}

public sealed record EmailMessage(string To, string Subject, string Html, string Text);

/// <summary>A failed send (09-notification §6.2 step 5). <see cref="Transient"/> is worth retrying; otherwise the failure is permanent. <see cref="Reason"/> is what goes to <c>last_error</c> (plan N17).</summary>
public sealed class EmailSendException(bool transient, string reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public string Reason { get; } = reason;
}
