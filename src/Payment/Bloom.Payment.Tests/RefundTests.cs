using Bloom.Payment.Consumers;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Stripe;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §6.2, §6.3, §7, §8; spec §4 "Refund completion", §7: the refund command, the provider executor and refund events (PR-04, PR-02). P-06, P-07, P-08.</summary>
public class RefundTests(Containers containers)
{
    private const string Refunds = "v1/refunds";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RefundPaymentV1 Command(Guid orderId, long amount, Guid? refundId = null, IReadOnlyList<RefundLineV1>? lines = null, string reason = "return") =>
        new(orderId, refundId ?? Guid.NewGuid(), amount, reason, lines);

    private static Task Send(TestPayment t, RefundPaymentV1 command) =>
        Consume.Async<RefundPaymentConsumer>(t, PaymentMessages.RefundPayment, command);

    private static async Task<int> RefundCountAsync(TestPayment t)
    {
        await using var db = t.NewDb();
        return await db.Refunds.CountAsync(Ct);
    }

    private static Task<IReadOnlyList<RefundCompletedV1>> Completed(TestPayment t) => Outbox.PayloadsAsync<RefundCompletedV1>(t, PaymentMessages.RefundCompleted);

    private static Task<IReadOnlyList<RefundFailedV1>> Failed(TestPayment t) => Outbox.PayloadsAsync<RefundFailedV1>(t, PaymentMessages.RefundFailed);

    private static IEnumerable<StripeRequest> Creates(TestPayment t) => t.Stripe.Requests.Where(r => r.Path == Refunds && r.Method == "POST");

    private static Task<HttpResponseMessage> Post(TestPayment t, string stripeRefundId, string status, Guid? refundId, string? failureReason = null, string? eventId = null) =>
        StripeEvents.PostAsync(t, StripeEvents.Json("refund.updated", StripeEvents.Refund(stripeRefundId, status, refundId, failureReason), t.Clock.GetUtcNow(), eventId));

    [Fact]
    public async Task Refund_command_records_intent_and_executor_completes_it()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, paymentId, piId) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400, lines: [new(1, 250), new(2, 150)]);

        await Send(t, command);

        var pending = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Pending, pending.Status);
        Assert.Equal((paymentId, orderId, 400L, "return", 0), (pending.PaymentId, pending.OrderId, pending.AmountCents, pending.Reason, pending.Attempts));
        Assert.Equal(t.Clock.GetUtcNow(), pending.NextAttemptAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(command.Lines, pending.Lines);
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Empty(Creates(t));

        await t.RunExecutorAsync();

        var request = Assert.Single(Creates(t));
        Assert.Equal($"bloom:refund:{command.RefundId:D}", request.IdempotencyKey);
        Assert.Equal("400", request.Form.Single(p => p.Key == "amount").Value);
        Assert.Equal(piId, request.Form.Single(p => p.Key == "payment_intent").Value);
        var done = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Succeeded, done.Status);
        Assert.Equal("re_test_1", done.StripeRefundId);
        var sent = Assert.Single(await Completed(t));
        Assert.Equal((orderId, command.RefundId, 400L), (sent.OrderId, sent.RefundId, sent.AmountCents));
        Assert.Equal(command.Lines, sent.Lines);
        Assert.Empty(await Failed(t));
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(1, metrics.Sum("refunds_total", ("status", "succeeded")));
        Assert.Equal(0, metrics.Sum("refunds_total", ("status", "submitted")));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task P06_refund_command_twice_makes_one_refund()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);

        await Send(t, command);
        await Send(t, command);
        await t.RunExecutorAsync();
        await t.RunExecutorAsync();

        Assert.Equal(1, await RefundCountAsync(t));
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Single(Creates(t));
        Assert.Single(await Completed(t));
    }

    [Fact]
    public async Task P07_over_limit_refund_is_dead_lettered()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        await Send(t, Command(orderId, 700));
        var over = Command(orderId, 400);

        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, over));

        await using var db = t.NewDb();
        Assert.False(await db.Refunds.AnyAsync(r => r.Id == over.RefundId, Ct));
        Assert.Equal(700, (await t.PaymentAsync(paymentId)).RefundedCents);
        var error = Assert.Single(t.Logs.Entries, e => e.StartsWith("[Error]", StringComparison.Ordinal));
        Assert.True(error.IndexOf(orderId.ToString("D"), StringComparison.Ordinal) < error.IndexOf(over.RefundId.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_refunds_never_exceed_the_amount()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await Send(t, Command(orderId, 300));
                return (Exception?)null;
            }
            catch (Exception e)
            {
                return e;
            }
        }));

        Assert.Equal(2, results.Count(r => r is PermanentFailureException));
        Assert.Equal(3, results.Count(r => r is null));
        Assert.Equal(3, await RefundCountAsync(t));
        Assert.Equal(900, (await t.PaymentAsync(paymentId)).RefundedCents);
        await t.RunExecutorAsync();
        Assert.Equal(3, Creates(t).Count());
    }

    [Fact]
    public async Task Refund_for_an_unpaid_or_unknown_payment_is_permanent()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (openOrder, _, _) = await Seed.CreateAsync(t, 1000);

        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(openOrder, 100)));
        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(Guid.NewGuid(), 100)));

        Assert.Equal(0, await RefundCountAsync(t));
        await using var db = t.NewDb();
        Assert.Equal(0, await db.Payments.CountAsync(p => p.RefundedCents != 0, Ct));
        Assert.Equal(2, t.ErrorLogs);
    }

    [Fact]
    public async Task Invalid_command_is_permanent()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);

        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(orderId, 0)));
        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(orderId, -5)));
        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(orderId, 100, reason: "")));
        await Assert.ThrowsAsync<PermanentFailureException>(() => Send(t, Command(orderId, 100, lines: [new(1, 150), new(2, -50)])));

        Assert.Equal(0, await RefundCountAsync(t));
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
    }

    [Fact]
    public async Task P08_stripe_refund_failure_releases_and_publishes_refund_failed()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "pending";
        var command = Command(orderId, 400);
        await Send(t, command);
        await t.RunExecutorAsync();
        var submitted = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Submitted, submitted.Status);
        Assert.Equal("re_test_1", submitted.StripeRefundId);
        Assert.Equal(1, metrics.Sum("refunds_total", ("status", "submitted")));
        Assert.Equal(0, t.ErrorLogs);

        using var posted = await Post(t, "re_test_1", "failed", command.RefundId, "expired_or_canceled_card");

        Assert.Equal(System.Net.HttpStatusCode.OK, posted.StatusCode);
        var failed = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Failed, failed.Status);
        Assert.Equal("expired_or_canceled_card", failed.FailureReason);
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(new RefundFailedV1(orderId, command.RefundId, "expired_or_canceled_card"), Assert.Single(await Failed(t)));
        Assert.Empty(await Completed(t));
        Assert.Equal(1, metrics.Sum("refunds_total", ("status", "failed")));
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Create_answering_failed_fails_at_once()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "failed";
        var command = Command(orderId, 400);
        await Send(t, command);

        await t.RunExecutorAsync();

        var failed = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Failed, failed.Status);
        Assert.Equal("re_test_1", failed.StripeRefundId);
        Assert.Equal("failed", failed.FailureReason); // Stripe gave no reason: the status itself (owner item 5)
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(new RefundFailedV1(orderId, command.RefundId, "failed"), Assert.Single(await Failed(t)));
        Assert.Empty(await Completed(t));
    }

    [Fact]
    public async Task Canceled_refund_status()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "canceled";
        var command = Command(orderId, 400);
        await Send(t, command);

        await t.RunExecutorAsync();

        var failed = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Failed, "re_test_1", "canceled"), (failed.Status, failed.StripeRefundId, failed.FailureReason));
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(new RefundFailedV1(orderId, command.RefundId, "canceled"), Assert.Single(await Failed(t)));
        Assert.Empty(await Completed(t));
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Requires_action_refund_status()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "requires_action";
        var command = Command(orderId, 400);
        await Send(t, command);

        await t.RunExecutorAsync();

        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Submitted, "re_test_1"), (row.Status, row.StripeRefundId));
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Empty(await Failed(t));
        Assert.Empty(await Completed(t));
        Assert.Equal(0, t.ErrorLogs); // a known status, unlike D24's unknown one
    }

    [Fact]
    public async Task Pending_refund_completes_by_webhook()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "pending";
        var command = Command(orderId, 400);
        await Send(t, command);
        await t.RunExecutorAsync();

        using var first = await Post(t, "re_test_1", "succeeded", command.RefundId);
        using var again = await Post(t, "re_test_1", "succeeded", command.RefundId); // a new event id

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(RefundStatuses.Succeeded, (await t.RefundAsync(command.RefundId)).Status);
        Assert.Single(await Completed(t));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Webhook_finds_an_unsubmitted_refund_by_metadata()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, piId) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        // The executor crashed after Stripe made the refund: the row is pending without a Stripe id.
        var made = await t.Services.GetRequiredService<IPaymentProvider>().CreateRefundAsync(command.RefundId, orderId, piId, 400, Ct);

        using var posted = await Post(t, made.Id, "succeeded", command.RefundId);

        Assert.Equal(System.Net.HttpStatusCode.OK, posted.StatusCode);
        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Succeeded, made.Id), (row.Status, row.StripeRefundId));
        Assert.Single(await Completed(t));

        await t.RunExecutorAsync();

        Assert.Single(Creates(t));
        Assert.Single(await Completed(t));
    }

    [Fact]
    public async Task Refund_event_for_an_unknown_refund_is_stored_only()
    {
        await using var t = await TestPayment.StartAsync(containers);
        await Seed.PaidAsync(t, 1000);

        using var posted = await Post(t, "re_dashboard", "succeeded", null);

        Assert.Equal(System.Net.HttpStatusCode.OK, posted.StatusCode);
        Assert.Empty(await Completed(t));
        Assert.Empty(await Failed(t));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Executor_and_webhook_settle_once()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        Task<HttpResponseMessage>? webhook = null;
        t.Stripe.Before = async path =>
        {
            if (path != Refunds) return;
            webhook = Post(t, "re_test_1", "succeeded", command.RefundId); // the executor holds the row lock
            await Poll.UntilAsync(async () => await t.LockWaitersAsync() > 0, 10, "the webhook waits for the executor's row lock"); // P-3: the interleaving is forced
        };

        await t.RunExecutorAsync();

        Assert.NotNull(webhook);
        using var response = await webhook;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RefundStatuses.Succeeded, (await t.RefundAsync(command.RefundId)).Status);
        Assert.Single(await Completed(t));
        Assert.Empty(await Failed(t));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Contradicting_status_on_a_final_refund_changes_nothing()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        await t.RunExecutorAsync();
        Assert.Equal(0, t.ErrorLogs);

        using var posted = await Post(t, "re_test_1", "failed", command.RefundId, "lost_or_stolen_card");

        Assert.Equal(System.Net.HttpStatusCode.OK, posted.StatusCode);
        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Succeeded, null), (row.Status, row.FailureReason));
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Empty(await Failed(t));
        Assert.Single(await Completed(t));
        Assert.Equal(0, metrics.Sum("refunds_total", ("status", "failed")));
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Transient_errors_follow_the_schedule_then_fail()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 500, times: 5);
        TimeSpan[] schedule = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await t.RunExecutorAsync();
            var row = await t.RefundAsync(command.RefundId);
            Assert.Equal((RefundStatuses.Pending, attempt), (row.Status, row.Attempts));
            var due = t.Clock.GetUtcNow() + schedule[attempt - 1];
            Assert.Equal(due, row.NextAttemptAt!.Value, TimeSpan.FromMilliseconds(1));

            await t.RunExecutorAsync(); // not due yet: no call
            Assert.Equal(attempt, Creates(t).Count());
            t.Clock.Advance(schedule[attempt - 1]);
        }
        Assert.Equal(0, t.ErrorLogs);

        await t.RunExecutorAsync();

        var failed = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Failed, "provider_unavailable", 5), (failed.Status, failed.FailureReason, failed.Attempts));
        Assert.Equal(5, Creates(t).Count());
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(new RefundFailedV1(orderId, command.RefundId, "provider_unavailable"), Assert.Single(await Failed(t)));
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Fifth_failure_leaves_a_refund_the_webhook_already_submitted()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 500, times: 5);
        TimeSpan[] schedule = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
        foreach (var delay in schedule)
        {
            await t.RunExecutorAsync();
            t.Clock.Advance(delay);
        }
        Task<HttpResponseMessage>? webhook = null;
        t.Stripe.Before = async path =>
        {
            if (path != Refunds) return;
            webhook = Post(t, "re_test_1", "pending", command.RefundId);
            await Poll.UntilAsync(async () => await t.LockWaitersAsync() > 0, 10, "the webhook waits for the executor's row lock");
        };

        await t.RunExecutorAsync(); // the 5th call fails, but the webhook gets the row first

        Assert.NotNull(webhook);
        using var response = await webhook;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, Creates(t).Count());
        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Submitted, row.Status);
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Empty(await Failed(t));
        Assert.Empty(await Completed(t));
    }

    [Fact]
    public async Task P1_no_retrying_log_when_guarded_update_hits_zero_rows()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 500);
        Task<HttpResponseMessage>? webhook = null;
        t.Stripe.Before = async path =>
        {
            if (path != Refunds) return;
            webhook = Post(t, "re_test_1", "pending", command.RefundId);
            await Poll.UntilAsync(async () => await t.LockWaitersAsync() > 0, 10, "the webhook waits for the executor's row lock");
        };

        await t.RunExecutorAsync(); // the 1st call fails, but the webhook submits the refund first: the guarded update hits 0 rows

        Assert.NotNull(webhook);
        using var response = await webhook;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Submitted, 0), (row.Status, row.Attempts));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("retrying at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task P1_fifth_failure_log_has_exception()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 500, times: 5);
        foreach (var delay in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.Zero })
        {
            await t.RunExecutorAsync();
            t.Clock.Advance(delay);
        }

        Assert.Equal(RefundStatuses.Failed, (await t.RefundAsync(command.RefundId)).Status);
        var giveUp = Assert.Single(t.Logs.Entries, e => e.Contains("giving up", StringComparison.Ordinal));
        Assert.Contains(nameof(ProviderException), giveUp);
    }

    [Fact]
    public async Task P4_transition_metric_not_recorded_on_rollback()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "pending";
        var command = Command(orderId, 400);
        await Send(t, command);
        await t.RunExecutorAsync();
        var submitted = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Submitted, submitted.Status);
        await t.SqlAsync($"ALTER TABLE outbox_messages ADD CONSTRAINT ck_test_never CHECK (false) NOT VALID"); // the transition runs, then saving its event fails

        using var failed = await Post(t, submitted.StripeRefundId!, "failed", command.RefundId, "lost_or_stolen_card");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(RefundStatuses.Submitted, (await t.RefundAsync(command.RefundId)).Status);
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(0, metrics.Sum("refunds_total", ("status", "failed")));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("cents released", StringComparison.Ordinal));
    }

    [Fact]
    public async Task D24_409_then_success_creates_one_refund()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 409, "idempotency_key_in_use");

        await t.RunExecutorAsync();

        var retrying = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Pending, 1), (retrying.Status, retrying.Attempts));
        t.Clock.Advance(TimeSpan.FromSeconds(1));

        await t.RunExecutorAsync();

        var done = await t.RefundAsync(command.RefundId);
        Assert.Equal(RefundStatuses.Succeeded, done.Status);
        Assert.Equal(2, Creates(t).Count());
        Assert.All(Creates(t), r => Assert.Equal($"bloom:refund:{command.RefundId:D}", r.IdempotencyKey)); // the retry reuses the key, so Stripe makes one refund
        Assert.Single(await Completed(t));
    }

    [Fact]
    public async Task D24_unknown_status_goes_submitted_no_second_create()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "some_new_status";
        var command = Command(orderId, 400);
        await Send(t, command);

        await t.RunExecutorAsync();

        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Submitted, "re_test_1"), (row.Status, row.StripeRefundId));
        Assert.Equal(1, t.ErrorLogs);
        Assert.Equal(400, (await t.PaymentAsync(paymentId)).RefundedCents);

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        await t.RunExecutorAsync(); // a later pass (or a restart) must not create again

        Assert.Single(Creates(t));
        Assert.Equal(RefundStatuses.Submitted, (await t.RefundAsync(command.RefundId)).Status);
    }

    [Fact]
    public async Task D24_other_4xx_still_permanent()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 404, "resource_missing");

        await t.RunExecutorAsync();
        t.Clock.Advance(TimeSpan.FromMinutes(5));
        await t.RunExecutorAsync();

        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Failed, "resource_missing"), (row.Status, row.FailureReason));
        Assert.Single(Creates(t));
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
    }

    [Fact]
    public async Task Permanent_error_fails_with_stripes_reason()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        var command = Command(orderId, 400);
        await Send(t, command);
        t.Stripe.Fail(Refunds, 400, "charge_already_refunded");

        await t.RunExecutorAsync();
        await t.RunExecutorAsync();

        var row = await t.RefundAsync(command.RefundId);
        Assert.Equal((RefundStatuses.Failed, "charge_already_refunded"), (row.Status, row.FailureReason));
        Assert.Single(Creates(t));
        Assert.Equal(0, (await t.PaymentAsync(paymentId)).RefundedCents);
        Assert.Equal(new RefundFailedV1(orderId, command.RefundId, "charge_already_refunded"), Assert.Single(await Failed(t)));
    }

    [Fact]
    public async Task One_refund_failing_does_not_block_the_others()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        var a = Command(orderId, 300);
        var b = Command(orderId, 300);
        await Send(t, a);
        await Send(t, b);
        t.Stripe.Fail(Refunds, 500);

        await t.RunExecutorAsync();

        var rows = new[] { await t.RefundAsync(a.RefundId), await t.RefundAsync(b.RefundId) };
        var failing = Assert.Single(rows, r => r.Status == RefundStatuses.Pending);
        var done = Assert.Single(rows, r => r.Status == RefundStatuses.Succeeded);
        Assert.Equal(1, failing.Attempts);
        Assert.NotEqual(failing.Id, done.Id);
        Assert.Single(await Completed(t));
    }
}
