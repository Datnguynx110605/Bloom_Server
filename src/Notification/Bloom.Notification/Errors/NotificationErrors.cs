using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Notification.Errors;

/// <summary>Error reasons of the notification service (spec §6). <c>ROLE_NOT_ALLOWED</c> comes from <see cref="BloomErrors"/>.</summary>
public static class NotificationErrors
{
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c> (plan N9).</summary>
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
}
