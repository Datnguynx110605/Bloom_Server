using System.Security.Cryptography;
using System.Text;

namespace Bloom.Fulfillment.Domain;

/// <summary>Deterministic ids (plan R12).</summary>
public static class Ids
{
    /// <summary>Fixed namespace of the reship ids; never change it: a retried <c>ConfirmLost(reship)</c> must derive the same id.</summary>
    private static readonly Guid ReshipNamespace = new("5e1f6a52-0b7c-4d57-9a3e-b100d0ea5a1c");

    /// <summary>
    /// The id of the reship of a lost shipment, per flag occurrence (D27): the shipment and the failure's <c>dismissed_at</c> (Unix ms, empty when never dismissed). A re-flag always follows a dismissal,
    /// so the id is stable within one flag and new after each dismissal.
    /// </summary>
    public static Guid ReshipId(Guid shipmentId, DateTimeOffset? dismissedAt) => NameBased(ReshipNamespace, $"{shipmentId:D}:{dismissedAt?.ToUnixTimeMilliseconds()}");

    /// <summary>RFC 9562 UUIDv5: SHA-1 of the namespace (network byte order) and the UTF-8 name, version and variant bits set.</summary>
    public static Guid NameBased(Guid ns, string name)
    {
        var hash = SHA1.HashData([.. ns.ToByteArray(bigEndian: true), .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash.AsSpan(0, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
