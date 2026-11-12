using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Bloom.Inventory.Errors;

/// <summary>Error reasons of 02-inventory §5.4 and the plan (owner item 4), each with its gRPC status. <c>CONCURRENCY_CONFLICT</c>, <c>ROLE_NOT_ALLOWED</c> and <c>IDEMPOTENCY_KEY_*</c> come from <see cref="BloomErrors"/>.</summary>
public static class InventoryErrors
{
    public const string SoldOutReason = "SOLD_OUT";
    public const string ReservationConflict = "RESERVATION_CONFLICT";
    public const string SkuNotStocked = "SKU_NOT_STOCKED";
    public const string SerialNotAvailable = "SERIAL_NOT_AVAILABLE";
    public const string SerialCountExceeded = "SERIAL_COUNT_EXCEEDED";
    public const string NotCommitted = "NOT_COMMITTED";
    public const string ReceiptInvalidReason = "RECEIPT_INVALID";
    public const string AdjustmentExceedsAvailable = "ADJUSTMENT_EXCEEDS_AVAILABLE";
    public const string SerialAssigned = "SERIAL_ASSIGNED";
    public const string OrderReleased = "ORDER_RELEASED";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string CatalogUnavailableReason = "CATALOG_UNAVAILABLE";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [SoldOutReason] = StatusCode.FailedPrecondition,
        [SkuNotStocked] = StatusCode.FailedPrecondition,
        [SerialNotAvailable] = StatusCode.FailedPrecondition,
        [SerialCountExceeded] = StatusCode.FailedPrecondition,
        [NotCommitted] = StatusCode.FailedPrecondition,
        [AdjustmentExceedsAvailable] = StatusCode.FailedPrecondition,
        [SerialAssigned] = StatusCode.FailedPrecondition,
        [OrderReleased] = StatusCode.FailedPrecondition,
        [ReservationConflict] = StatusCode.InvalidArgument,
        [ReceiptInvalidReason] = StatusCode.InvalidArgument,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotFound] = StatusCode.NotFound,
        [CatalogUnavailableReason] = StatusCode.Unavailable,
    };

    public static RpcException Error(string reason, string message) => BloomErrors.Create(Codes[reason], reason, message);

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c>.</summary>
    public static RpcException Validation(string field, string message) =>
        Build(ValidationFailed, message, new() { ["field"] = field });

    /// <summary><c>SOLD_OUT</c> with <c>ErrorInfo.Metadata["skuId"]</c>.</summary>
    public static RpcException SoldOut(Guid skuId) =>
        Build(SoldOutReason, "Not enough stock.", new() { ["skuId"] = skuId.ToString() });

    /// <summary><c>RECEIPT_INVALID</c>: the <c>ErrorInfo</c> plus one <c>google.rpc.BadRequest</c> with a violation per problem, in the order given (plan R13).</summary>
    public static RpcException ReceiptInvalid(IReadOnlyList<(string Field, string Description)> violations)
    {
        var bad = new BadRequest();
        bad.FieldViolations.AddRange(violations.Select(v => new BadRequest.Types.FieldViolation { Field = v.Field, Description = v.Description }));
        return Build(ReceiptInvalidReason, "The goods receipt is invalid.", [], bad);
    }

    public static RpcException CatalogUnavailable() => Error(CatalogUnavailableReason, "The catalog is unavailable; retry shortly.");

    private static RpcException Build(string reason, string message, Dictionary<string, string> metadata, BadRequest? bad = null)
    {
        var info = new ErrorInfo { Reason = reason, Domain = BloomErrors.Domain };
        foreach (var (key, value) in metadata) info.Metadata[key] = value;
        var status = new Status { Code = (int)Codes[reason], Message = message, Details = { Google.Protobuf.WellKnownTypes.Any.Pack(info) } };
        if (bad is not null) status.Details.Add(Google.Protobuf.WellKnownTypes.Any.Pack(bad));
        return status.ToRpcException();
    }
}
