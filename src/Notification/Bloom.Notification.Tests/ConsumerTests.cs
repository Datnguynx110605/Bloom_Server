using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Identity.Contracts;
using Bloom.Notification.Data;
using Bloom.Notification.Templates;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Tests;

/// <summary>09-notification §5.3, §6.1, §6.5, §8 through <c>EventConsumer</c> (plan N6, N11, N12, N16, owner item 2).</summary>
public class ConsumerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal const string Number = "BL-1001";

    // One database per host (plan N11), so every test can reuse this order id.
    internal static readonly Guid Order = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static TheoryData<string, string, string[]> NotifyingMessages => new()
    {
        { IdentityMessages.VerificationCode, NotificationTypes.AccountCode, ["purpose", "encryptedCode", "expiresAt"] },
        { IdentityMessages.StaffInvited, NotificationTypes.StaffInvitation, ["role", "inviteUrl"] },
        { OrderMessages.Paid, NotificationTypes.OrderConfirmed, [] },
        { OrderMessages.Cancelled, NotificationTypes.OrderCancelled, ["reason"] },
        { OrderMessages.PreorderDelayed, NotificationTypes.OrderPreorderDelayed, [] },
        { FulfillmentMessages.ShipmentShipped, NotificationTypes.OrderShipped, ["trackingNumber"] },
        { FulfillmentMessages.ShipmentDelivered, NotificationTypes.OrderDelivered, [] },
        { FulfillmentMessages.DeliveryFailed, NotificationTypes.OrderDeliveryFailed, ["kind"] },
        { FulfillmentMessages.ReturnLabelIssued, NotificationTypes.ReturnLabelIssued, ["labelUrl"] },
        { FulfillmentMessages.ReturnReceived, NotificationTypes.ReturnReceived, [] },
        { FulfillmentMessages.ReturnDecided, NotificationTypes.ReturnDecided, ["refundAmount"] },
        { FulfillmentMessages.ReturnClosed, NotificationTypes.ReturnExpired, [] },
        { PaymentMessages.RefundCompleted, NotificationTypes.RefundIssued, ["refundAmount"] },
    };

    internal static object Payload(string messageType, Guid account, string code = "482913") => messageType switch
    {
        IdentityMessages.VerificationCode => new VerificationCodeV1("Code@Bloom.test", "signup", TestNotification.EncryptCode(code), new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)),
        IdentityMessages.StaffInvited => new StaffInvitedV1("staff@bloom.test", "support", "https://admin.test/invite/abc"),
        OrderMessages.Paid => new OrderPaidV1(Order, Number, account, null, DateTimeOffset.UtcNow, 1000, []),
        OrderMessages.Cancelled => new OrderCancelledV1(Order, Number, account, "late_payment"),
        OrderMessages.PreorderDelayed => new OrderPreorderDelayedV1(Order, Number, account),
        FulfillmentMessages.ShipmentShipped => new ShipmentShippedV1(Order, Order, [], "TRK-9"),
        FulfillmentMessages.ShipmentDelivered => new ShipmentDeliveredV1(Order, Order, DateTimeOffset.UtcNow),
        FulfillmentMessages.DeliveryFailed => new DeliveryFailedV1(Order, "lost"),
        FulfillmentMessages.ReturnLabelIssued => new ReturnLabelIssuedV1(Order, Guid.NewGuid(), "https://labels.test/l.pdf"),
        FulfillmentMessages.ReturnReceived => new ReturnReceivedV1(Order, Guid.NewGuid()),
        FulfillmentMessages.ReturnDecided => new ReturnDecidedV1(Order, Guid.NewGuid(), [], 123456),
        FulfillmentMessages.ReturnClosed => new ReturnClosedV1(Order, Guid.NewGuid(), "expired"),
        PaymentMessages.RefundCompleted => new RefundCompletedV1(Order, Guid.NewGuid(), 2500, null),
        _ => throw new ArgumentOutOfRangeException(nameof(messageType)),
    };

    private static async Task<Guid> CustomerAsync(TestNotification t, string name = "Ada")
    {
        var account = Guid.NewGuid();
        await Seed.ContactAsync(t, account, $"{account:N}@bloom.test", name);
        await Seed.OrderRefAsync(t, Order, account, Number);
        return account;
    }

    private static async Task<List<EmailJobRow>> JobsAsync(TestNotification t, Guid? account = null, Guid? source = null)
    {
        await using var db = t.NewDb();
        return await db.EmailJobs.AsNoTracking().Where(j => (account == null || j.AccountId == account) && (source == null || j.SourceMessageId == source)).ToListAsync(Ct);
    }

    private static Dictionary<string, string> Data(EmailJobRow job) => JsonSerializer.Deserialize<Dictionary<string, string>>(job.Data)!;

    [Fact]
    public async Task N01_order_paid_twice_makes_one_row_one_job_one_push()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await CustomerAsync(t);
        await using var c = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));
        var inbox = Sockets.Inbox(c);
        var id = Guid.NewGuid();

        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id);
        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id);

        var pushed = await Sockets.NextAsync(inbox);
        Assert.Equal("order.confirmed", pushed.Type);
        await Task.Delay(500, Ct);
        Assert.False(inbox.Reader.TryRead(out _), "a second push arrived");
        Assert.Single(await Seed.RowsAsync(t, account));
        Assert.Single(await JobsAsync(t, account));
        Assert.Equal(1, metrics.Sum("ws_pushes_total", ("type", "order.confirmed")));
        Assert.Equal(1, metrics.Sum("notifications_created_total", ("type", "order.confirmed"), ("channel", "in_app")));
        Assert.Equal(1, metrics.Sum("notifications_created_total", ("type", "order.confirmed"), ("channel", "email")));
    }

    [Theory, MemberData(nameof(NotifyingMessages))]
    public async Task Each_message_maps_to_its_type(string messageType, string type, string[] dataKeys)
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        var id = Guid.NewGuid();

        await Consume.Async(t, messageType, Payload(messageType, account), id);

        var emailOnly = !NotificationTypes.InApp.Contains(type);
        var job = Assert.Single(await JobsAsync(t, source: id));
        Assert.Equal(type, job.Template);
        Assert.Equal(EmailStatuses.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        var expectedKeys = emailOnly ? dataKeys : [.. dataKeys, "orderNumber", "name"];
        Assert.Equal(expectedKeys.Order(), Data(job).Keys.Order());
        if (emailOnly)
        {
            Assert.Null(job.AccountId);
            Assert.Empty(await Seed.RowsAsync(t, account));
            Assert.Equal(type == "account.code" ? "Code@Bloom.test" : "staff@bloom.test", job.ToEmail);
            return;
        }
        Assert.Equal(account, job.AccountId);
        Assert.Equal($"{account:N}@bloom.test", job.ToEmail);
        Assert.Equal(Number, Data(job)["orderNumber"]);
        Assert.Equal("Ada", Data(job)["name"]);
        var row = Assert.Single(await Seed.RowsAsync(t, account));
        Assert.Equal(type, row.Type);
        Assert.Equal($"/orders/{Number}", row.Link);
        Assert.Equal(id, row.SourceMessageId);
        Assert.False(string.IsNullOrWhiteSpace(row.Title));
        Assert.False(string.IsNullOrWhiteSpace(row.Body));
    }

    [Fact]
    public async Task In_app_text_stores_markup_raw()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        await Seed.ContactAsync(t, account, $"{account:N}@bloom.test", "Ada");
        const string number = "<script>x</script> & \"q\"";

        await Consume.Async(t, OrderMessages.Paid, new OrderPaidV1(Order, number, account, null, t.Clock.GetUtcNow(), 1000, []));

        var row = Assert.Single(await Seed.RowsAsync(t, account));
        Assert.Equal($"We received your payment for order {number}.", row.Body); // escaping is the client's job; the stored text is raw
    }

    [Fact]
    public async Task N02_event_before_its_order_ref_is_transient()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        await Seed.ContactAsync(t, account, "a@bloom.test", "Ada");
        var shipped = Guid.NewGuid();

        var e = await Assert.ThrowsAnyAsync<Exception>(() => Consume.Async(t, FulfillmentMessages.ShipmentShipped, Payload(FulfillmentMessages.ShipmentShipped, account), shipped));
        Assert.IsNotType<PermanentFailureException>(e);
        Assert.Empty(await Seed.RowsAsync(t, account));
        Assert.Empty(await JobsAsync(t, source: shipped));

        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account));
        await Consume.Async(t, FulfillmentMessages.ShipmentShipped, Payload(FulfillmentMessages.ShipmentShipped, account), shipped);
        Assert.Single(await JobsAsync(t, source: shipped));
    }

    [Fact]
    public async Task Missing_contact_is_transient()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        var id = Guid.NewGuid();

        var e = await Assert.ThrowsAnyAsync<Exception>(() => Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id));
        Assert.IsNotType<PermanentFailureException>(e);
        Assert.Empty(await JobsAsync(t, source: id));
        Assert.Empty(await Seed.RowsAsync(t, account));

        await Seed.ContactAsync(t, account, "a@bloom.test", "Ada");
        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id);
        Assert.Single(await JobsAsync(t, source: id));
    }

    [Fact]
    public async Task Code_job_keeps_the_code_encrypted()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var id = Guid.NewGuid();
        var payload = (VerificationCodeV1)Payload(IdentityMessages.VerificationCode, Guid.NewGuid(), "739104");

        await Consume.Async(t, IdentityMessages.VerificationCode, payload, id);

        var job = Assert.Single(await JobsAsync(t, source: id));
        var data = Data(job);
        Assert.Equal("signup", data["purpose"]);
        Assert.Equal(payload.EncryptedCode, data["encryptedCode"]);
        Assert.Equal(payload.ExpiresAt, DateTimeOffset.Parse(data["expiresAt"]));
        Assert.DoesNotContain("739104", job.Data);
        Assert.Equal(["encryptedCode", "expiresAt", "purpose"], data.Keys.Order());
    }

    [Fact]
    public async Task Return_closed_notifies_only_when_expired()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        var decided = Guid.NewGuid();

        await Consume.Async(t, FulfillmentMessages.ReturnClosed, new ReturnClosedV1(Order, Guid.NewGuid(), "decided"), decided);
        Assert.Empty(await Seed.RowsAsync(t, account));
        Assert.Empty(await JobsAsync(t, source: decided));

        var expired = Guid.NewGuid();
        await Consume.Async(t, FulfillmentMessages.ReturnClosed, Payload(FulfillmentMessages.ReturnClosed, account), expired);
        Assert.Equal("return.expired", Assert.Single(await Seed.RowsAsync(t, account)).Type);
        Assert.Equal("return.expired", Assert.Single(await JobsAsync(t, source: expired)).Template);
    }

    [Fact]
    public async Task Late_payment_refund_resolves_through_the_cancelled_ref()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        await Seed.ContactAsync(t, account, "a@bloom.test", "Ada");
        var refunded = Guid.NewGuid();

        await Consume.Async(t, OrderMessages.Cancelled, Payload(OrderMessages.Cancelled, account));
        await Consume.Async(t, PaymentMessages.RefundCompleted, Payload(PaymentMessages.RefundCompleted, account), refunded);

        var job = Assert.Single(await JobsAsync(t, source: refunded));
        Assert.Equal("refund.issued", job.Template);
        Assert.Equal("$25.00", Data(job)["refundAmount"]);
        Assert.Equal(Number, Data(job)["orderNumber"]);
        Assert.Contains(await Seed.RowsAsync(t, account), r => r.Type == "refund.issued");
    }

    [Fact]
    public async Task Contact_change_keeps_the_address_of_queued_jobs()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        var first = Guid.NewGuid();
        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), first);

        await Consume.Async(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "New@Bloom.test", "Ada"), occurredAt: t.Clock.GetUtcNow().UtcDateTime.AddMinutes(1));
        var second = Guid.NewGuid();
        await Consume.Async(t, FulfillmentMessages.ShipmentShipped, Payload(FulfillmentMessages.ShipmentShipped, account), second);

        Assert.Equal($"{account:N}@bloom.test", Assert.Single(await JobsAsync(t, source: first)).ToEmail);
        Assert.Equal("New@Bloom.test", Assert.Single(await JobsAsync(t, source: second)).ToEmail);
    }

    [Fact]
    public async Task Older_contact_event_does_not_overwrite_a_newer_one()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        var t1 = t.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5);
        var t2 = t.Clock.GetUtcNow().UtcDateTime;

        await Consume.Async(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "t2@bloom.test", "Two"), occurredAt: t2);
        await Consume.Async(t, IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account, "t1@bloom.test", "One"), occurredAt: t1);

        await using var db = t.NewDb();
        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.AccountId == account, Ct);
        Assert.Equal("t2@bloom.test", contact.Email);
        Assert.Equal("Two", contact.Name);
    }

    [Fact]
    public async Task Push_happens_only_after_commit()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        await using var c = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));
        var inbox = Sockets.Inbox(c);
        var id = Guid.NewGuid();

        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id, rollback: true);

        await Task.Delay(1000, Ct);
        Assert.False(inbox.Reader.TryRead(out _), "pushed before the commit");
        Assert.Empty(await Seed.RowsAsync(t, account));
        Assert.Empty(await JobsAsync(t, source: id));

        var before = await t.DbNowAsync();
        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account), id);
        var after = await t.DbNowAsync();

        var pushed = await Sockets.NextAsync(inbox);
        Assert.Equal("order.confirmed", pushed.Type);
        await Task.Delay(300, Ct);
        Assert.False(inbox.Reader.TryRead(out _), "more than one push");
        var stored = Assert.Single(await Seed.RowsAsync(t, account));
        Assert.InRange(stored.CreatedAt, before, after);
        Assert.Equal(stored.CreatedAt, pushed.CreatedAt, TimeSpan.FromMilliseconds(1)); // the push carries the stored time
        var job = Assert.Single(await JobsAsync(t, source: id));
        Assert.InRange(job.CreatedAt, before, after);
        Assert.Equal(job.CreatedAt, job.NextAttemptAt); // due at once
    }

    [Fact]
    public async Task Offline_customer_sees_it_on_next_load()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        await Consume.Async(t, OrderMessages.Paid, Payload(OrderMessages.Paid, account));

        using var client = t.CustomerClient(account);
        var response = await client.GetAsync("/api/notifications", Ct);
        var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        var row = Assert.Single(page.GetProperty("notifications").EnumerateArray());
        Assert.Equal("order.confirmed", row.GetProperty("type").GetString());
        Assert.False(row.TryGetProperty("readAt", out var readAt) && readAt.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task N10_account_deleted_removes_data_and_cancels_pending_jobs()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var a = await CustomerAsync(t);
        var b = Guid.NewGuid();
        await Seed.ContactAsync(t, b, "b@bloom.test", "Bo");
        await Seed.OrderRefAsync(t, Guid.NewGuid(), b, "BL-2002");
        await Seed.NotificationAsync(t, a, t.Clock.GetUtcNow());
        await Seed.NotificationAsync(t, b, t.Clock.GetUtcNow());
        Guid pendingA, sentA, pendingB;
        await using (var db = t.NewDb())
        {
            EmailJobRow Job(Guid account, string status) => Seed.Job(t, status: status, createdAt: t.Clock.GetUtcNow(), accountId: account);
            var jobs = new[] { Job(a, EmailStatuses.Pending), Job(a, EmailStatuses.Sent), Job(b, EmailStatuses.Pending) };
            (pendingA, sentA, pendingB) = (jobs[0].Id, jobs[1].Id, jobs[2].Id);
            db.EmailJobs.AddRange(jobs);
            await db.SaveChangesAsync(Ct);
        }
        await using var socket = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, a));

        await Consume.Async(t, IdentityMessages.AccountDeleted, new AccountDeletedV1(a));

        await using var check = t.NewDb();
        Assert.False(await check.Contacts.AnyAsync(x => x.AccountId == a, Ct));
        Assert.False(await check.Notifications.AnyAsync(x => x.AccountId == a, Ct));
        Assert.False(await check.OrderRefs.AnyAsync(x => x.AccountId == a, Ct));
        Assert.Equal(EmailStatuses.Cancelled, (await check.EmailJobs.SingleAsync(x => x.Id == pendingA, Ct)).Status);
        Assert.Equal(EmailStatuses.Sent, (await check.EmailJobs.SingleAsync(x => x.Id == sentA, Ct)).Status);
        Assert.Equal(EmailStatuses.Pending, (await check.EmailJobs.SingleAsync(x => x.Id == pendingB, Ct)).Status);
        Assert.True(await check.Contacts.AnyAsync(x => x.AccountId == b, Ct));
        Assert.True(await check.Notifications.AnyAsync(x => x.AccountId == b, Ct));
        Assert.True(await check.OrderRefs.AnyAsync(x => x.AccountId == b, Ct));
        await Sockets.ClosedAsync(socket, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Tokens_revoked_closes_the_users_sockets()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = await CustomerAsync(t);
        await Seed.NotificationAsync(t, account, t.Clock.GetUtcNow());
        await using var socket = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));

        await Consume.Async(t, IdentityMessages.TokensRevoked, new TokensRevokedV1(account, 1));

        await Sockets.ClosedAsync(socket, TimeSpan.FromSeconds(2));
        await using var db = t.NewDb();
        Assert.True(await db.Contacts.AnyAsync(x => x.AccountId == account, Ct));
        Assert.Single(await Seed.RowsAsync(t, account));
    }
}
