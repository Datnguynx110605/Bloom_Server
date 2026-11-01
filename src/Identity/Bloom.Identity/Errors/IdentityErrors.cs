using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Bloom.Identity.Errors;

/// <summary>Error reasons of 06-identity §5.3 and spec §4, each with its gRPC status.</summary>
public static class IdentityErrors
{
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string MfaInvalid = "MFA_INVALID";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string RegistrationTokenInvalid = "REGISTRATION_TOKEN_INVALID";
    public const string PasswordWeak = "PASSWORD_WEAK";
    public const string CodeInvalid = "CODE_INVALID";
    public const string CodeRateLimited = "CODE_RATE_LIMITED";
    public const string ReauthRequired = "REAUTH_REQUIRED";
    public const string GoogleNotAllowed = "GOOGLE_NOT_ALLOWED";
    public const string RefreshInvalid = "REFRESH_INVALID";
    public const string UnsettledOrders = "UNSETTLED_ORDERS";
    public const string OrderServiceUnavailable = "ORDER_SERVICE_UNAVAILABLE";
    public const string LastAdmin = "LAST_ADMIN";
    public const string InvitationInvalid = "INVITATION_INVALID";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string AddressLimitReached = "ADDRESS_LIMIT_REACHED";
    public const string PasswordRequired = "PASSWORD_REQUIRED";
    public const string GoogleAlreadyLinked = "GOOGLE_ALREADY_LINKED";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [InvalidCredentials] = StatusCode.Unauthenticated,
        [MfaInvalid] = StatusCode.Unauthenticated,
        [RefreshInvalid] = StatusCode.Unauthenticated,
        [EmailAlreadyRegistered] = StatusCode.AlreadyExists,
        [EmailTaken] = StatusCode.AlreadyExists,
        [GoogleAlreadyLinked] = StatusCode.AlreadyExists,
        [PasswordWeak] = StatusCode.InvalidArgument,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotFound] = StatusCode.NotFound,
        [CodeRateLimited] = StatusCode.ResourceExhausted,
        [OrderServiceUnavailable] = StatusCode.Unavailable,
        [RegistrationTokenInvalid] = StatusCode.FailedPrecondition,
        [CodeInvalid] = StatusCode.FailedPrecondition,
        [ReauthRequired] = StatusCode.FailedPrecondition,
        [GoogleNotAllowed] = StatusCode.FailedPrecondition,
        [UnsettledOrders] = StatusCode.FailedPrecondition,
        [LastAdmin] = StatusCode.FailedPrecondition,
        [InvitationInvalid] = StatusCode.FailedPrecondition,
        [AddressLimitReached] = StatusCode.FailedPrecondition,
        [PasswordRequired] = StatusCode.FailedPrecondition,
    };

    public static RpcException Error(string reason, string message) => BloomErrors.Create(Codes[reason], reason, message);

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c>.</summary>
    public static RpcException Validation(string field, string message) =>
        new Status
        {
            Code = (int)StatusCode.InvalidArgument,
            Message = message,
            Details =
            {
                Google.Protobuf.WellKnownTypes.Any.Pack(new ErrorInfo
                {
                    Reason = ValidationFailed, Domain = BloomErrors.Domain, Metadata = { ["field"] = field },
                }),
            },
        }.ToRpcException();
}
