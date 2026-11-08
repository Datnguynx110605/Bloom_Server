using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Payment.Errors;

/// <summary>Error reasons of 04-payment §5.3 and the spec, each with its gRPC status. <c>ROLE_NOT_ALLOWED</c> comes from <see cref="BloomErrors"/>.</summary>
public static class PaymentErrors
{
    public const string ExistsDifferentAmount = "PAYMENT_EXISTS_DIFFERENT_AMOUNT";
    public const string CreateInProgress = "PAYMENT_CREATE_IN_PROGRESS";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFoundReason = "NOT_FOUND";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [ExistsDifferentAmount] = StatusCode.InvalidArgument,
        [CreateInProgress] = StatusCode.Aborted,
        [ProviderUnavailable] = StatusCode.Unavailable,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotFoundReason] = StatusCode.NotFound,
        [BloomErrors.RoleNotAllowed] = StatusCode.PermissionDenied,
    };

    public static RpcException Error(string reason, string message) => BloomErrors.Create(Codes[reason], reason, message);

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c> (plan R9).</summary>
    public static RpcException Validation(string field, string message)
    {
        var info = new Google.Rpc.ErrorInfo { Reason = ValidationFailed, Domain = BloomErrors.Domain, Metadata = { ["field"] = field } };
        return new Google.Rpc.Status
        {
            Code = (int)Codes[ValidationFailed],
            Message = message,
            Details = { Google.Protobuf.WellKnownTypes.Any.Pack(info) },
        }.ToRpcException();
    }

    public static RpcException NotFound(string what) => Error(NotFoundReason, $"{what} was not found.");
}
