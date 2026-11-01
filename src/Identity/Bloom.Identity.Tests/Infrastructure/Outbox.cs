using System.Buffers.Text;
using System.Text;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Tests;

public static class Outbox
{
    /// <summary>Envelopes of the given message type in the outbox, oldest first.</summary>
    public static async Task<IReadOnlyList<Envelope>> EnvelopesAsync(TestIdentity t, string messageType)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == messageType).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(p))).ToList();
    }

    /// <summary>The code in the newest <c>verification-code</c> envelope for the email and purpose, decrypted with the host's code key.</summary>
    public static async Task<string> LatestCodeAsync(TestIdentity t, string email, string purpose)
    {
        var message = (await EnvelopesAsync(t, IdentityMessages.VerificationCode))
            .Select(e => (e.OccurredAt, Payload: e.GetPayload<VerificationCodeV1>()))
            .Where(x => x.Payload.Email == email && x.Payload.Purpose == purpose)
            .OrderBy(x => x.OccurredAt).Last().Payload;
        return Encoding.UTF8.GetString(new SecretBox(t.Secrets.CodeEncryptionKey).Open(Base64Url.DecodeFromChars(message.EncryptedCode)));
    }
}
