using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Bloom.Shared.Errors;

/// <summary>Error contract (00-reliability §8): gRPC status plus <c>google.rpc.ErrorInfo{domain="bloom", reason}</c>.</summary>
public static class BloomErrors
{
    public const string Domain = "bloom";

    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyInvalid = "IDEMPOTENCY_KEY_INVALID";
    public const string RoleNotAllowed = "ROLE_NOT_ALLOWED";
    public const string DlqBusy = "DLQ_BUSY";
    public const string DlqNotFound = "DLQ_NOT_FOUND";
    public const string DlqMessageNotFound = "DLQ_MESSAGE_NOT_FOUND";

    public static RpcException Create(StatusCode code, string reason, string message) =>
        new Status
        {
            Code = (int)code,
            Message = message,
            Details = { Google.Protobuf.WellKnownTypes.Any.Pack(new ErrorInfo { Reason = reason, Domain = Domain }) },
        }.ToRpcException();

    /// <summary>Conditional update rejected (§7.5); the reason is defined by the owning service.</summary>
    public static RpcException FailedPrecondition(string reason, string message) =>
        Create(StatusCode.FailedPrecondition, reason, message);
}
