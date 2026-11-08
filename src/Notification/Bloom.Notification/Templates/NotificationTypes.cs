namespace Bloom.Notification.Templates;

/// <summary>The 13 notification types of 09-notification §5.3; each is also the name of its template file.</summary>
public static class NotificationTypes
{
    public const string AccountCode = "account.code";
    public const string StaffInvitation = "staff.invitation";
    public const string OrderConfirmed = "order.confirmed";
    public const string OrderCancelled = "order.cancelled";
    public const string OrderPreorderDelayed = "order.preorder-delayed";
    public const string OrderShipped = "order.shipped";
    public const string OrderDelivered = "order.delivered";
    public const string OrderDeliveryFailed = "order.delivery-failed";
    public const string ReturnLabelIssued = "return.label-issued";
    public const string ReturnReceived = "return.received";
    public const string ReturnDecided = "return.decided";
    public const string ReturnExpired = "return.expired";
    public const string RefundIssued = "refund.issued";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        AccountCode, StaffInvitation, OrderConfirmed, OrderCancelled, OrderPreorderDelayed, OrderShipped, OrderDelivered,
        OrderDeliveryFailed, ReturnLabelIssued, ReturnReceived, ReturnDecided, ReturnExpired, RefundIssued,
    };

    /// <summary>Types that also create an in-app row: all but the two email-only ones (NR-02).</summary>
    public static readonly IReadOnlySet<string> InApp = All.Except([AccountCode, StaffInvitation]).ToHashSet();
}
