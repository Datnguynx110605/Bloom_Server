using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Fulfillment.Errors;

/// <summary>Error reasons of 05-fulfillment §5.4 and the spec (<c>INVALID_STATE</c>, <c>NOT_FOUND</c>, <c>VALIDATION_FAILED</c>), each with its gRPC status. <c>CONCURRENCY_CONFLICT</c>, <c>ROLE_NOT_ALLOWED</c> and <c>IDEMPOTENCY_KEY_*</c> come from <see cref="BloomErrors"/>; inventory's reasons pass through unchanged.</summary>
public static class FulfillmentErrors
{
    public const string PackingStarted = "PACKING_STARTED";
    public const string AlreadyShipped = "ALREADY_SHIPPED";
    public const string SerialsIncomplete = "SERIALS_INCOMPLETE";
    public const string LabelNotReady = "LABEL_NOT_READY";
    public const string ReturnWindowClosed = "RETURN_WINDOW_CLOSED";
    public const string ReturnQuantityExceeded = "RETURN_QUANTITY_EXCEEDED";
    public const string InvalidReturnState = "INVALID_RETURN_STATE";
    public const string PartialExceedsShare = "PARTIAL_EXCEEDS_SHARE";
    public const string ReshipNoStock = "RESHIP_NO_STOCK";
    public const string InvalidState = "INVALID_STATE";
    public const string NotFoundReason = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [PackingStarted] = StatusCode.FailedPrecondition,
        [AlreadyShipped] = StatusCode.FailedPrecondition,
        [SerialsIncomplete] = StatusCode.FailedPrecondition,
        [LabelNotReady] = StatusCode.FailedPrecondition,
        [ReturnWindowClosed] = StatusCode.FailedPrecondition,
        [InvalidReturnState] = StatusCode.FailedPrecondition,
        [ReshipNoStock] = StatusCode.FailedPrecondition,
        [InvalidState] = StatusCode.FailedPrecondition,
        [ReturnQuantityExceeded] = StatusCode.InvalidArgument,
        [PartialExceedsShare] = StatusCode.InvalidArgument,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotFoundReason] = StatusCode.NotFound,
    };

    public static RpcException Error(string reason, string message) => BloomErrors.Create(Codes[reason], reason, message);

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c> (plan R10).</summary>
    public static RpcException Validation(string field, string message)
    {
        var info = new Google.Rpc.ErrorInfo { Reason = ValidationFailed, Domain = BloomErrors.Domain, Metadata = { ["field"] = field } };
        return new Google.Rpc.Status
        {
            Code = (int)StatusCode.InvalidArgument,
            Message = message,
            Details = { Google.Protobuf.WellKnownTypes.Any.Pack(info) },
        }.ToRpcException();
    }

    public static RpcException NotFound(string what) => Error(NotFoundReason, $"{what} was not found.");
}
