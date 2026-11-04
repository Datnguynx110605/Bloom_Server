using Bloom.Inventory.Domain;

namespace Bloom.Inventory.Errors;

/// <summary>Request-field parsing for the gRPC services (plan R14, R15): a bad value is <c>VALIDATION_FAILED</c> naming the proto field.</summary>
public static class Inputs
{
    public static Guid Id(string value, string field) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw InventoryErrors.Validation(field, $"{field} must be a UUID.");

    public static string Serial(string value, string field) =>
        Identifiers.NormalizeSerial(value) ?? throw InventoryErrors.Validation(field, $"{field} must be 1-32 letters or digits.");
}
