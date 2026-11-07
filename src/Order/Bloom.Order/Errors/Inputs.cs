using System.Text.RegularExpressions;
using Bloom.Order.Api;
using Bloom.Order.Contracts;
using Bloom.Order.Data;

namespace Bloom.Order.Errors;

/// <summary>Request-field parsing for the gRPC services (plan R9): a bad value is <c>VALIDATION_FAILED</c> naming the proto field; a bad address is <c>ADDRESS_INVALID</c>.</summary>
public static partial class Inputs
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private static readonly HashSet<string> States =
    [
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO",
        "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
        "DC", "AS", "GU", "MP", "PR", "VI", "AA", "AE", "AP",
    ];

    [GeneratedRegex(@"^\d{5}(-\d{4})?\z")]
    private static partial Regex ZipPattern();

    [GeneratedRegex(@"^\+[1-9]\d{1,14}\z")]
    private static partial Regex PhonePattern();

    [GeneratedRegex("^[A-Za-z0-9-]{3,32}$")]
    private static partial Regex CouponPattern();

    public static Guid Id(string value, string field) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw OrderErrors.Validation(field, $"{field} must be a UUID.");

    /// <summary><c>page</c> 0 is 1, <c>page_size</c> 0 is 50 and at most 100. Returns the row offset (capped at <c>int.MaxValue</c>, past any real table) and the page size.</summary>
    public static (int Skip, int Take) Paging(int page, int pageSize)
    {
        if (page < 0) throw OrderErrors.Validation("page", "The page must not be negative.");
        if (pageSize is < 0 or > MaxPageSize) throw OrderErrors.Validation("page_size", $"The page size is 1 to {MaxPageSize}.");
        var size = pageSize == 0 ? DefaultPageSize : pageSize;
        return ((int)Math.Min((Math.Max(page, 1) - 1L) * size, int.MaxValue), size);
    }

    /// <summary>The address rules of 03-order §9: name, lines and city are trimmed and 1 to 100 characters; <c>lines</c> has 1 or 2 items.</summary>
    public static ShipToV1 ShipTo(ShipTo proto)
    {
        var name = Text(proto.Name, "name");
        if (proto.Lines.Count is < 1 or > 2) throw OrderErrors.Address("lines", "lines must have 1 or 2 items.");
        var lines = proto.Lines.Select(l => Text(l, "lines")).ToList();
        var city = Text(proto.City, "city");
        if (!States.Contains(proto.State)) throw OrderErrors.Address("state", "state must be a USPS state code.");
        if (!ZipPattern().IsMatch(proto.Zip)) throw OrderErrors.Address("zip", "zip must be 12345 or 12345-6789.");
        if (!PhonePattern().IsMatch(proto.Phone)) throw OrderErrors.Address("phone", "phone must be in E.164 format, for example +17755550100.");
        return new ShipToV1(name, lines, city, proto.State, proto.Zip, proto.Phone);

        static string Text(string value, string field) =>
            value.Trim() is { Length: >= 1 and <= 100 } t ? t : throw OrderErrors.Address(field, $"{field} must be 1 to 100 characters.");
    }

    /// <summary>The launch source of 03-order §6.2 step 3, no I/O: a launch id, and lines that are non-empty, with quantity at least 1, at least one product, no duplicate (kind, sku, parent).</summary>
    public static Guid LaunchLines(LaunchSource source)
    {
        var launchId = Id(source.LaunchId, "launch_id");
        if (source.Lines.Count == 0) throw OrderErrors.Validation("lines", "lines must not be empty.");
        foreach (var l in source.Lines)
        {
            if (l.Kind is not (LineKinds.Product or LineKinds.WarrantyPlan)) throw OrderErrors.Validation("lines", "A line kind must be product or warranty_plan.");
            Id(l.SkuId, "lines");
            if (l.Quantity < 1) throw OrderErrors.Validation("lines", "A line quantity must be at least 1.");
        }
        if (!source.Lines.Any(l => l.Kind == LineKinds.Product)) throw OrderErrors.Validation("lines", "A launch order needs at least one product line.");
        if (source.Lines.GroupBy(l => (l.Kind, l.SkuId, l.HasParentLineNo ? l.ParentLineNo : (int?)null)).Any(g => g.Count() > 1))
            throw OrderErrors.Validation("lines", "A line must not repeat.");
        return launchId;
    }

    /// <summary>Trimmed, 3 to 32 characters of <c>A-Za-z0-9-</c>. <c>Normalized</c> is upper case (what is stored and compared), <c>Display</c> keeps the case the customer typed.</summary>
    public static (string Normalized, string Display) CouponCode(string value, string field)
    {
        var display = value.Trim();
        return CouponPattern().IsMatch(display)
            ? (display.ToUpperInvariant(), display)
            : throw OrderErrors.Validation(field, $"{field} must be 3 to 32 letters, digits or hyphens.");
    }
}
