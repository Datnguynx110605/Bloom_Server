using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace Bloom.Notification.Email;

/// <summary>SMTP (Mailpit locally) over <c>System.Net.Mail</c> (spec §7). SMTP has no idempotency key, so a crash between send and update can resend (accepted, NR-01).</summary>
public sealed class SmtpEmailProvider(NotificationOptions options) : IEmailProvider
{
    public async Task<string?> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct)
    {
        var smtp = options.Email.Smtp;
        using var client = new SmtpClient(smtp.Host, smtp.Port);
        if (!string.IsNullOrEmpty(smtp.User)) client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); // SmtpClient.Timeout does not apply to SendMailAsync
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var mail = new MailMessage(options.Email.From, message.To) { Subject = message.Subject, Body = message.Text };
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, null, MediaTypeNames.Text.Html));
            await client.SendMailAsync(mail, timeout.Token);
            return null;
        }
        catch (FormatException e) // a malformed address will never be accepted (no message: it would echo the address)
        {
            throw new EmailSendException(false, "invalid_address", "The recipient address is malformed.", e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) // our 10 s; the caller's own cancellation propagates
        {
            throw new EmailSendException(true, "timeout", "The SMTP server did not answer in time.", e);
        }
        catch (SmtpException e) // never log or echo the address or body
        {
            throw new EmailSendException(!IsPermanent(e.StatusCode), $"smtp_{(int)e.StatusCode}", $"SMTP answered {(int)e.StatusCode}.", e);
        }
    }

    /// <summary>A 5xx mailbox status: the address or message will never be accepted.</summary>
    public static bool IsPermanent(SmtpStatusCode code) => code is
        SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.UserNotLocalTryAlternatePath or SmtpStatusCode.ExceededStorageAllocation or SmtpStatusCode.MailboxNameNotAllowed;
}
