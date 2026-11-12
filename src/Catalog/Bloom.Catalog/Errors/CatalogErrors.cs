using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Status = Google.Rpc.Status;

namespace Bloom.Catalog.Errors;

/// <summary>Error reasons of 08-catalog §5 and the spec, each with its gRPC status. <c>CONCURRENCY_CONFLICT</c> and <c>ROLE_NOT_ALLOWED</c> come from <see cref="BloomErrors"/>.</summary>
public static class CatalogErrors
{
    public const string SerializedLocked = "SERIALIZED_LOCKED";
    public const string DuplicateCombination = "DUPLICATE_COMBINATION";
    public const string CategoryDepth = "CATEGORY_DEPTH";
    public const string InvalidLifecycle = "INVALID_LIFECYCLE";
    public const string PublishIncomplete = "PUBLISH_INCOMPLETE";
    public const string SkuCodeTaken = "SKU_CODE_TAKEN";
    public const string NotVerifiedBuyer = "NOT_VERIFIED_BUYER";
    public const string ImageInvalid = "IMAGE_INVALID";
    public const string NotFound = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string SlugTaken = "SLUG_TAKEN";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [SerializedLocked] = StatusCode.FailedPrecondition,
        [InvalidLifecycle] = StatusCode.FailedPrecondition,
        [PublishIncomplete] = StatusCode.FailedPrecondition,
        [DuplicateCombination] = StatusCode.AlreadyExists,
        [SkuCodeTaken] = StatusCode.AlreadyExists,
        [SlugTaken] = StatusCode.AlreadyExists,
        [CategoryDepth] = StatusCode.InvalidArgument,
        [ImageInvalid] = StatusCode.InvalidArgument,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [NotVerifiedBuyer] = StatusCode.PermissionDenied,
        [NotFound] = StatusCode.NotFound,
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

    /// <summary>Named apart from the <see cref="NotFound"/> reason constant, which R8 passes to <c>UpdateVersionedAsync</c>.</summary>
    public static RpcException NotFoundError(string what) => Error(NotFound, $"{what} not found.");
}
