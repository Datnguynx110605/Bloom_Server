using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Order.Errors;

/// <summary>Error reasons of 03-order §5.4 and the spec, each with its gRPC status. <c>CONCURRENCY_CONFLICT</c>, <c>ROLE_NOT_ALLOWED</c> and <c>IDEMPOTENCY_KEY_*</c> come from <see cref="BloomErrors"/>; inventory's and launch's reasons pass through unchanged.</summary>
public static class OrderErrors
{
    public const string CartEmpty = "CART_EMPTY";
    public const string SkuNotBuyable = "SKU_NOT_BUYABLE";
    public const string LaunchItemsInCart = "LAUNCH_ITEMS_IN_CART";
    public const string MixedLaunchOrder = "MIXED_LAUNCH_ORDER";
    public const string QuantityLimit = "QUANTITY_LIMIT";
    public const string WarrantyNotEligible = "WARRANTY_NOT_ELIGIBLE";
    public const string AddressInvalid = "ADDRESS_INVALID";
    public const string CouponNotFound = "COUPON_NOT_FOUND";
    public const string CouponInactive = "COUPON_INACTIVE";
    public const string CouponNotStarted = "COUPON_NOT_STARTED";
    public const string CouponExpired = "COUPON_EXPIRED";
    public const string CouponMinSpend = "COUPON_MIN_SPEND";
    public const string CouponNotApplicable = "COUPON_NOT_APPLICABLE";
    public const string CouponLimitReached = "COUPON_LIMIT_REACHED";
    public const string CancelNotAllowed = "CANCEL_NOT_ALLOWED";
    public const string AlreadyShipped = "ALREADY_SHIPPED";
    public const string CouponCodeTaken = "COUPON_CODE_TAKEN";
    public const string CheckoutClosed = "CHECKOUT_CLOSED";
    public const string NotFoundReason = "NOT_FOUND";
    public const string ValidationFailed = "VALIDATION_FAILED";

    private static readonly Dictionary<string, StatusCode> Codes = new()
    {
        [CartEmpty] = StatusCode.FailedPrecondition,
        [SkuNotBuyable] = StatusCode.FailedPrecondition,
        [LaunchItemsInCart] = StatusCode.InvalidArgument,
        [MixedLaunchOrder] = StatusCode.InvalidArgument,
        [QuantityLimit] = StatusCode.InvalidArgument,
        [WarrantyNotEligible] = StatusCode.InvalidArgument,
        [AddressInvalid] = StatusCode.InvalidArgument,
        [CancelNotAllowed] = StatusCode.FailedPrecondition,
        [AlreadyShipped] = StatusCode.FailedPrecondition,
        [CouponCodeTaken] = StatusCode.AlreadyExists,
        [CheckoutClosed] = StatusCode.FailedPrecondition,
        [NotFoundReason] = StatusCode.NotFound,
        [ValidationFailed] = StatusCode.InvalidArgument,
        [CouponNotFound] = StatusCode.FailedPrecondition,
        [CouponInactive] = StatusCode.FailedPrecondition,
        [CouponNotStarted] = StatusCode.FailedPrecondition,
        [CouponExpired] = StatusCode.FailedPrecondition,
        [CouponMinSpend] = StatusCode.FailedPrecondition,
        [CouponNotApplicable] = StatusCode.FailedPrecondition,
        [CouponLimitReached] = StatusCode.FailedPrecondition,
    };

    public static RpcException Error(string reason, string message) => BloomErrors.Create(Codes[reason], reason, message);

    /// <summary><c>VALIDATION_FAILED</c> with <c>ErrorInfo.Metadata["field"]</c> (plan R9).</summary>
    public static RpcException Validation(string field, string message) => WithField(ValidationFailed, field, message);

    /// <summary><c>ADDRESS_INVALID</c> with <c>ErrorInfo.Metadata["field"]</c> naming the <c>ship_to</c> field (plan R9).</summary>
    public static RpcException Address(string field, string message) => WithField(AddressInvalid, field, message);

    /// <summary><c>CONCURRENCY_CONFLICT</c>: the <paramref name="what"/> changed since the caller read it.</summary>
    public static RpcException Conflict(string what) =>
        BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, $"The {what} was changed by someone else; reload and retry.");

    public static RpcException NotFound(string what) => Error(NotFoundReason, $"{what} was not found.");

    private static RpcException WithField(string reason, string field, string message)
    {
        var info = new Google.Rpc.ErrorInfo { Reason = reason, Domain = BloomErrors.Domain, Metadata = { ["field"] = field } };
        return new Google.Rpc.Status
        {
            Code = (int)Codes[reason],
            Message = message,
            Details = { Google.Protobuf.WellKnownTypes.Any.Pack(info) },
        }.ToRpcException();
    }
}
