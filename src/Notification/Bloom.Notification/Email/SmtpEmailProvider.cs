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
        using var mail = new MailMessage(options.Email.From, message.To) { Subject = message.Subject, Body = message.Text };
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, null, MediaTypeNames.Text.Html));
        try
        {
            await client.SendMailAsync(mail, ct);
            return null;
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
