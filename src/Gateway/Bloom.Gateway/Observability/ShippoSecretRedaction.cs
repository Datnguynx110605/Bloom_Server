using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Bloom.Gateway;

/// <summary>The Shippo webhook URL carries a secret (<c>/webhooks/shippo/{secret}</c>); spans show it as <c>/webhooks/shippo/***</c> (LLD 07 §9, D15).</summary>
public static partial class ShippoSecretRedaction
{
    private static readonly string[] Tags = ["url.path", "http.target", "url.full"];

    [GeneratedRegex("(?<=/webhooks/shippo/)[^/?#]+")]
    private static partial Regex Secret();

    public static void Redact(Activity activity)
    {
        foreach (var tag in Tags)
            if (activity.GetTagItem(tag) is string value && Secret().IsMatch(value))
                activity.SetTag(tag, Secret().Replace(value, "***"));
    }
}
