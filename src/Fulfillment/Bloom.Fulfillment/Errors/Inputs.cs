namespace Bloom.Fulfillment.Errors;

/// <summary>Request-field parsing for the gRPC services (plan R10): a bad value is <c>VALIDATION_FAILED</c> naming the proto field.</summary>
public static class Inputs
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    public static Guid Id(string value, string field) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw FulfillmentErrors.Validation(field, $"{field} must be a UUID.");

    /// <summary>Trimmed and upper-cased; 1 to 32 characters of <c>A-Z0-9</c>.</summary>
    public static string Serial(string value, string field)
    {
        var s = value.Trim().ToUpperInvariant();
        return s.Length is >= 1 and <= 32 && s.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            ? s
            : throw FulfillmentErrors.Validation(field, $"{field} must be 1-32 letters or digits.");
    }

    /// <summary><c>page</c> 0 is 1, <c>page_size</c> 0 is 50 and at most 100. Returns the row offset (capped at <c>int.MaxValue</c>, past any real table) and the page size.</summary>
    public static (int Skip, int Take) Paging(int page, int pageSize)
    {
        if (page < 0) throw FulfillmentErrors.Validation("page", "The page must not be negative.");
        if (pageSize is < 0 or > MaxPageSize) throw FulfillmentErrors.Validation("page_size", $"The page size is 1 to {MaxPageSize}.");
        var size = pageSize == 0 ? DefaultPageSize : pageSize;
        return ((int)Math.Min((Math.Max(page, 1) - 1L) * size, int.MaxValue), size);
    }
}
