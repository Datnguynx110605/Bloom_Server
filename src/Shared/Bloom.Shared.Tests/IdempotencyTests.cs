using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bloom.Shared.Errors;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Persistence;
using Bloom.Shared.Tests.Protos;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

public class IdempotencyTests(Containers containers)
{
    private const string Method = "/bloom.order.v1.OrderService/StartCheckout";
    private const string Operation = "bloom.order.v1.OrderService/StartCheckout";
    private static readonly ChargeRequest Req = new() { Sku = "rose", Quantity = 2 };

    private sealed class Calls { public int N; }

    /// <summary>A provider that, like Stripe, dedupes by idempotency key.</summary>
    private sealed class FakeProvider
    {
        private readonly ConcurrentDictionary<string, string> _results = new();
        public int Invocations;
        public int Effects => _results.Count;

        public Task<string> CallAsync(string key)
        {
            Interlocked.Increment(ref Invocations);
            return Task.FromResult(_results.GetOrAdd(key, _ => "ch_" + Guid.NewGuid().ToString("N")));
        }
    }

    private static FakeServerCallContext Ctx(Guid user, string? key, string method = Method)
    {
        var headers = new Metadata { { "x-bloom-user-id", user.ToString() } };
        if (key is not null) headers.Add("idempotency-key", key);
        return new FakeServerCallContext(method, headers);
    }

    private static string Reason(RpcException ex) => ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason;

    private static async Task<RpcException> Fails(Task t, StatusCode code, string reason)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => t);
        Assert.Equal(code, ex.StatusCode);
        Assert.Equal(reason, Reason(ex));
        return ex;
    }

    /// <summary>Single-transaction call in its own scope. The handler inserts an <c>effects</c> row and counts invocations.</summary>
    private static async Task<ChargeResponse> Single(IHost host, FakeServerCallContext ctx, Calls calls, ChargeRequest? req = null,
        Func<TestDbContext, Task>? fail = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IdempotencyGuard>().ExecuteAsync(req ?? Req, ctx, async _ =>
        {
            Interlocked.Increment(ref calls.N);
            db.Effects.Add(new Effect { Consumer = "idem", MessageId = Guid.NewGuid() });
            if (fail is not null) await fail(db);
            return new ChargeResponse { ChargeId = Guid.NewGuid().ToString() };
        });
    }

    /// <summary>Two-phase call in its own scope; <c>apply</c> inserts an effect and returns the provider's charge id.</summary>
    private static async Task<ChargeResponse> TwoPhase(IHost host, FakeServerCallContext ctx, FakeProvider provider, Calls applied,
        Func<string, Task<string>>? callProvider = null, Func<TestDbContext, Task>? failApply = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IdempotencyGuard>().ExecuteWithProviderAsync(Req, ctx,
            (key, _) => callProvider?.Invoke(key) ?? provider.CallAsync(key),
            async (charge, _) =>
            {
                Interlocked.Increment(ref applied.N);
                db.Effects.Add(new Effect { Consumer = "idem", MessageId = Guid.NewGuid() });
                if (failApply is not null) await failApply(db);
                return new ChargeResponse { ChargeId = charge };
            });
    }

    private static async Task<int> Count<T>(IHost host, CancellationToken ct) where T : class
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<T>().CountAsync(ct);
    }

    private static async Task<IdempotencyKey> OnlyKey(IHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<IdempotencyKey>().AsNoTracking().SingleAsync(ct);
    }

    /// <summary>Saves the handler's pending effect, then fails, so only the guard's rollback can undo it.</summary>
    private static async Task Throw(TestDbContext db, Exception ex)
    {
        await db.SaveChangesAsync();
        throw ex;
    }

    private static string ServiceOf(IHost host) => host.Services.GetRequiredService<BloomOptions>().ServiceName;

    /// <summary>The row a crashed worker would leave behind after tx 1.</summary>
    private static async Task InsertInProgress(IHost host, Guid user, string key, ChargeRequest req, double lockedForSeconds, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var hash = SHA256.HashData(req.ToByteArray());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO idempotency_keys (user_id, key, operation, request_hash, status, locked_until)
            VALUES ({user}, {key}, {Operation}, {hash}, 'in_progress', now() + make_interval(secs => {lockedForSeconds}))
            """, ct);
    }

    [Fact]
    public async Task T10_new_key_executes_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new ConcurrencyTests.Capture(ServiceOf(host));
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();

        var response = await Single(host, Ctx(user, key), calls);

        var row = await OnlyKey(host, ct);
        Assert.Equal(user, row.UserId);
        Assert.Equal(key, row.Key);
        Assert.Equal(Operation, row.Operation);
        Assert.Equal("completed", row.Status);
        Assert.Equal(0, row.ResponseStatus);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(response.ToByteArray(), row.ResponseBody);
        Assert.Equal(SHA256.HashData(Req.ToByteArray()), row.RequestHash);
        Assert.Equal(1, calls.N);
        Assert.Equal(1, capture.Sum("idempotency_requests_total", "result", "new"));
    }

    [Fact]
    public async Task T11_same_key_same_request_replays()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new ConcurrencyTests.Capture(ServiceOf(host));
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();

        var first = await Single(host, Ctx(user, key), calls);
        var second = await Single(host, Ctx(user, key), calls);

        Assert.Equal(first.ToByteArray(), second.ToByteArray());
        Assert.Equal(1, calls.N);
        Assert.Equal(1, await Count<Effect>(host, ct));
        Assert.Equal(1, capture.Sum("idempotency_requests_total", "result", "replay"));
    }

    [Fact]
    public async Task T12_same_key_different_request_is_reused()
    {
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new ConcurrencyTests.Capture(ServiceOf(host));
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();
        await Single(host, Ctx(user, key), calls);

        await Fails(Single(host, Ctx(user, key), calls, new ChargeRequest { Sku = "rose", Quantity = 3 }),
            StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyReused);

        Assert.Equal(1, calls.N);
        Assert.Equal(1, capture.Sum("idempotency_requests_total", "result", "reused"));
    }

    [Fact]
    public async Task Same_key_different_operation_is_reused()
    {
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();
        await Single(host, Ctx(user, key), calls);

        await Fails(Single(host, Ctx(user, key, "/bloom.order.v1.OrderService/OtherOperation"), calls),
            StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyReused);
        Assert.Equal(1, calls.N);
    }

    [Fact]
    public async Task T13_single_tx_concurrent_duplicate_gets_replay()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Single(host, Ctx(user, key), calls, fail: async _ => { entered.SetResult(); await gate.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var second = Single(host, Ctx(user, key), calls);

        await Task.Delay(500, ct);
        Assert.False(second.IsCompleted); // blocked on the primary key until the first commits
        gate.SetResult();
        var (a, b) = (await first, await second);

        Assert.Equal(a.ToByteArray(), b.ToByteArray());
        Assert.Equal(1, calls.N);
        Assert.Equal(1, await Count<Effect>(host, ct));
    }

    [Fact]
    public async Task T13_two_phase_concurrent_duplicate_in_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var applied = new Calls();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = TwoPhase(host, Ctx(user, key), provider, applied, async k => { entered.SetResult(); await gate.Task; return await provider.CallAsync(k); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await Fails(TwoPhase(host, Ctx(user, key), provider, applied), StatusCode.Aborted, BloomErrors.IdempotencyInProgress);
        gate.SetResult();
        await first;

        Assert.Equal(1, applied.N);
        Assert.Equal(1, provider.Effects);
        Assert.Equal("completed", (await OnlyKey(host, ct)).Status);
    }

    [Fact]
    public async Task T14_crash_after_provider_call_is_resumed_after_lock_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new ConcurrencyTests.Capture(ServiceOf(host));
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var applied = new Calls();
        var charge = await provider.CallAsync($"bloom:{Operation}:{user}:{key}"); // the call made before the crash
        await InsertInProgress(host, user, key, Req, -1, ct);

        var response = await TwoPhase(host, Ctx(user, key), provider, applied);

        Assert.Equal(charge, response.ChargeId);
        Assert.Equal(2, provider.Invocations);
        Assert.Equal(1, provider.Effects);
        Assert.Equal(1, applied.N);
        var row = await OnlyKey(host, ct);
        Assert.Equal("completed", row.Status);
        Assert.Equal(response.ToByteArray(), row.ResponseBody);
        Assert.Equal(1, capture.Sum("idempotency_requests_total", "result", "takeover"));
    }

    [Fact]
    public async Task Request_that_lost_its_lock_to_a_takeover_does_not_complete_the_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var appliedByOriginal = new Calls();
        var appliedByTaker = new Calls();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The original request parks in its provider call after tx 1 ...
        var original = TwoPhase(host, Ctx(user, key), provider, appliedByOriginal, callProvider: async k =>
        {
            parked.SetResult();
            await resume.Task;
            return await provider.CallAsync(k);
        });
        await parked.Task.WaitAsync(ct);
        // ... its lock expires, and a retry takes the key over and completes it.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database
                .ExecuteSqlAsync($"UPDATE idempotency_keys SET locked_until = now() - interval '1 second'", ct);
        var taker = await TwoPhase(host, Ctx(user, key), provider, appliedByTaker);

        // The original resumes: it no longer owns the key, so it must not apply or overwrite; it gets the replay.
        resume.SetResult();
        var replayed = await original;

        Assert.Equal(taker.ChargeId, replayed.ChargeId);
        Assert.Equal(1, appliedByTaker.N);
        Assert.Equal(1, await Count<Effect>(host, ct));
        var row = await OnlyKey(host, ct);
        Assert.Equal("completed", row.Status);
        Assert.Equal(taker.ToByteArray(), row.ResponseBody);
    }

    [Fact]
    public async Task T14_unexpired_lock_is_in_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        using var capture = new ConcurrencyTests.Capture(ServiceOf(host));
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        await InsertInProgress(host, user, key, Req, 30, ct);

        await Fails(TwoPhase(host, Ctx(user, key), provider, new Calls()), StatusCode.Aborted, BloomErrors.IdempotencyInProgress);

        Assert.Equal(0, provider.Invocations);
        Assert.Equal(1, capture.Sum("idempotency_requests_total", "result", "in_progress"));
    }

    [Fact]
    public async Task Stale_key_taken_over_by_exactly_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var applied = new Calls();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await InsertInProgress(host, user, key, Req, -1, ct);

        // The winner parks at the provider, so the first call to finish is the loser.
        Task<ChargeResponse> Retry() => TwoPhase(host, Ctx(user, key), provider, applied,
            async k => { entered.TrySetResult(); await gate.Task; return await provider.CallAsync(k); });
        var calls = new[] { Retry(), Retry() };
        var loser = await Task.WhenAny(calls).WaitAsync(TimeSpan.FromSeconds(30), ct);
        var winner = calls.Single(c => c != loser);
        await Fails(loser, StatusCode.Aborted, BloomErrors.IdempotencyInProgress);
        Assert.False(winner.IsCompleted);
        gate.SetResult();
        await winner;

        Assert.Equal(1, applied.N);
        Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task T15_business_rejection_is_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();
        Task Reject(TestDbContext db) => Throw(db, BloomErrors.FailedPrecondition("SOLD_OUT", "No stock."));

        await Fails(Single(host, Ctx(user, key), calls, fail: Reject), StatusCode.FailedPrecondition, "SOLD_OUT");
        var replayed = await Fails(Single(host, Ctx(user, key), calls), StatusCode.FailedPrecondition, "SOLD_OUT");

        Assert.Equal("No stock.", replayed.GetRpcStatus()!.Message);
        Assert.Equal(1, calls.N);
        Assert.Equal(0, await Count<Effect>(host, ct)); // the failed attempt's effect was rolled back
        var row = await OnlyKey(host, ct);
        Assert.Equal("completed", row.Status);
        Assert.Equal((int)StatusCode.FailedPrecondition, row.ResponseStatus);
    }

    [Fact]
    public async Task T15_two_phase_business_rejection_is_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var applied = new Calls();
        Task Reject(TestDbContext db) => Throw(db, BloomErrors.FailedPrecondition("SOLD_OUT", "No stock."));

        await Fails(TwoPhase(host, Ctx(user, key), provider, applied, failApply: Reject), StatusCode.FailedPrecondition, "SOLD_OUT");
        await Fails(TwoPhase(host, Ctx(user, key), provider, applied), StatusCode.FailedPrecondition, "SOLD_OUT");

        Assert.Equal(1, applied.N);
        Assert.Equal(1, provider.Invocations);
        Assert.Equal(0, await Count<Effect>(host, ct));
        Assert.Equal("completed", (await OnlyKey(host, ct)).Status);
    }

    [Fact]
    public async Task T16_transient_failure_releases_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var calls = new Calls();
        var transients = new Exception[] { new RpcException(new Grpc.Core.Status(StatusCode.Unavailable, "down")), new InvalidOperationException("boom") };

        for (var i = 0; i < transients.Length; i++)
        {
            var transient = transients[i];
            var key = Guid.NewGuid().ToString();
            var user = Guid.NewGuid();

            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Single(host, Ctx(user, key), calls, fail: db => Throw(db, transient)));
            Assert.Same(transient, thrown);
            Assert.Equal(i, await Count<IdempotencyKey>(host, ct)); // only the earlier iterations' completed rows
            Assert.Equal(i, await Count<Effect>(host, ct));         // the failed attempt's effect was rolled back

            await Single(host, Ctx(user, key), calls);
            Assert.Equal(2 * (i + 1), calls.N);
            Assert.Equal(i + 1, await Count<Effect>(host, ct));
        }
    }

    [Fact]
    public async Task T16_two_phase_transient_failure_releases_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var provider = new FakeProvider();
        var applied = new Calls();
        var unavailable = new RpcException(new Grpc.Core.Status(StatusCode.Unavailable, "provider down"));

        // The provider fails: the in-progress row from tx 1 is deleted.
        await Assert.ThrowsAsync<RpcException>(() => TwoPhase(host, Ctx(user, key), provider, applied, callProvider: _ => throw unavailable));
        Assert.Equal(0, await Count<IdempotencyKey>(host, ct));

        // tx 2 fails with a non-deterministic error: released as well, effects rolled back.
        await Assert.ThrowsAsync<RpcException>(() => TwoPhase(host, Ctx(user, key), provider, applied,
            failApply: db => Throw(db, unavailable)));
        Assert.Equal(0, await Count<IdempotencyKey>(host, ct));
        Assert.Equal(0, await Count<Effect>(host, ct));

        var response = await TwoPhase(host, Ctx(user, key), provider, applied);
        Assert.NotEmpty(response.ChargeId);
        Assert.Equal("completed", (await OnlyKey(host, ct)).Status);
        Assert.Equal(1, provider.Effects);
    }

    [Fact]
    public async Task Malformed_keys_rejected_before_database()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var calls = new Calls();
        var guid = Guid.NewGuid();

        await Fails(Single(host, Ctx(user, null), calls), StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyRequired);
        foreach (var bad in new[] { "", "abc", guid + "0", "{" + guid + "}", guid.ToString("N"), new string('z', 36), " " + guid, guid + " " })
            await Fails(Single(host, Ctx(user, bad), calls), StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyInvalid);

        Assert.Equal(0, calls.N);
        Assert.Equal(0, await Count<IdempotencyKey>(host, ct));
    }

    [Fact]
    public async Task Missing_user_header_is_a_gateway_misconfiguration()
    {
        using var host = await TestApp.BuildAsync(containers);
        var noUser = new FakeServerCallContext(Method, new Metadata { { "idempotency-key", Guid.NewGuid().ToString() } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Single(host, noUser, new Calls()));
    }

    [Fact]
    public async Task Key_case_variants_are_one_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();

        var first = await Single(host, Ctx(user, key.ToUpperInvariant()), calls);
        var second = await Single(host, Ctx(user, key.ToLowerInvariant()), calls);

        Assert.Equal(first.ToByteArray(), second.ToByteArray());
        Assert.Equal(1, calls.N);
        Assert.Equal(key.ToLowerInvariant(), (await OnlyKey(host, ct)).Key);
    }

    [Fact]
    public async Task Same_key_two_users_do_not_interfere()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers);
        var key = Guid.NewGuid().ToString();
        var calls = new Calls();

        await Single(host, Ctx(Guid.NewGuid(), key), calls);
        await Single(host, Ctx(Guid.NewGuid(), key), calls);

        Assert.Equal(2, calls.N);
        Assert.Equal(2, await Count<IdempotencyKey>(host, ct));
        Assert.Equal(2, await Count<Effect>(host, ct));
    }

    [Fact]
    public async Task Provider_key_format()
    {
        using var host = await TestApp.BuildAsync(containers);
        var user = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();
        string? seen = null;

        await TwoPhase(host, Ctx(user, key.ToUpperInvariant()), new FakeProvider(), new Calls(), callProvider: k => { seen = k; return Task.FromResult("ch_1"); });

        Assert.Equal($"bloom:{Operation}:{user}:{key}", seen);
    }
}
