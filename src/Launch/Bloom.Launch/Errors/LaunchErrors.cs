using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Launch.Errors;

/// <summary>Error reasons of 01-launch §5.4 and the spec (§4, §9.3), each with its gRPC status. <c>ROLE_NOT_ALLOWED</c> and <c>CONCURRENCY_CONFLICT</c> come from <see cref="BloomErrors"/>.</summary>
public static class LaunchErrors
{
    public const string WaitingRoomNotOpen = "WAITING_ROOM_NOT_OPEN";
    public const string WaitingRoomClosed = "WAITING_ROOM_CLOSED";
    public const string PassInvalid = "PASS_INVALID";
    public const string PassExpired = "PASS_EXPIRED";
    public const string PassAlreadyUsed = "PASS_ALREADY_USED";
    public const string LaunchNotOpen = "LAUNCH_NOT_OPEN";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string SoldOut = "SOLD_OUT";
    public const string AllocationDecreaseNotAllowed = "ALLOCATION_DECREASE_NOT_ALLOWED";
    public const string PurchaseLimitLocked = "PURCHASE_LIMIT_LOCKED";
    public const string InvalidStage = "INVALID_STAGE";
    public const string SkuNotInLaunch = "SKU_NOT_IN_LAUNCH";
    public const string HoldConflict = "HOLD_CONFLICT";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFoundReason = "NOT_FOUND";
    public const string NotInQueue = "NOT_IN_QUEUE";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [WaitingRoomNotOpen] = StatusCode.FailedPrecondition,
        [WaitingRoomClosed] = StatusCode.FailedPrecondition,
        [PassInvalid] = StatusCode.FailedPrecondition,
        [PassExpired] = StatusCode.FailedPrecondition,
        [PassAlreadyUsed] = StatusCode.FailedPrecondition,
        [LaunchNotOpen] = StatusCode.FailedPrecondition,
        [LimitExceeded] = StatusCode.FailedPrecondition,
        [SoldOut] = StatusCode.FailedPrecondition,
        [AllocationDecreaseNotAllowed] = StatusCode.FailedPrecondition,
        [PurchaseLimitLocked] = StatusCode.FailedPrecondition,
        [InvalidStage] = StatusCode.FailedPrecondition,
        [SkuNotInLaunch] = StatusCode.InvalidArgument,
        [HoldConflict] = StatusCode.InvalidArgument,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotFoundReason] = StatusCode.NotFound,
        [NotInQueue] = StatusCode.NotFound,
        [BloomErrors.RoleNotAllowed] = StatusCode.PermissionDenied,
        [BloomErrors.ConcurrencyConflict] = StatusCode.Aborted,
    };

    /// <summary>The reason's status with <c>ErrorInfo{domain="bloom"}</c> and the <paramref name="metadata"/> pairs (for example <c>productId</c>, <c>remaining</c>).</summary>
    public static RpcException Error(string reason, string message, params (string Key, string Value)[] metadata)
    {
        var info = new Google.Rpc.ErrorInfo { Reason = reason, Domain = BloomErrors.Domain };
        foreach (var (key, value) in metadata) info.Metadata[key] = value;
        return new Google.Rpc.Status
        {
            Code = (int)Codes[reason],
            Message = message,
            Details = { Google.Protobuf.WellKnownTypes.Any.Pack(info) },
        }.ToRpcException();
    }

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c> (plan R9).</summary>
    public static RpcException Validation(string field, string message) => Error(ValidationFailed, message, ("field", field));

    public static RpcException NotFound(string what) => Error(NotFoundReason, $"{what} was not found.");

    /// <summary>A failed dependency (owner item 2, order's upstream failures): plain <c>UNAVAILABLE</c>, no <c>bloom</c> reason.</summary>
    public static RpcException Unavailable(string message) => new(new Status(StatusCode.Unavailable, message));
}
