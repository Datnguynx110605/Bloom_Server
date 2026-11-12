using System.Globalization;

namespace Bloom.Notification.Templates;

public static class Money
{
    /// <summary>Cents as US dollars: <c>123456</c> → <c>$1,234.56</c> (plan N16).</summary>
    public static string Format(long cents) => (cents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
