using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Launch.Tests;

public class HoldTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync() => TestLaunch.StartAsync(containers);

    private static string Id(Guid id) => id.ToString("D");

    private static Task Fails(Task<HoldAllocationResponse> call, string reason) => Rpc.FailsAsync(call, reason);

    private static async Task<(int Held, int Sold)> SkuAsync(TestLaunch t, Guid sku)
    {
        await using var db = t.NewDb();
        var row = await db.LaunchSkus.AsNoTracking().SingleAsync(s => s.SkuId == sku, Ct);
        return (row.Held, row.Sold);
    }

    private static async Task<int> HoldsAsync(TestLaunch t, Guid launch)
    {
        await using var db = t.NewDb();
        return await db.Holds.CountAsync(h => h.LaunchId == launch, Ct);
    }

    private static async Task<int> ConsumedAsync(TestLaunch t, Guid launch)
    {
        await using var db = t.NewDb();
        return await db.ConsumedPasses.CountAsync(p => p.LaunchId == launch, Ct);
    }

    private static async Task NothingWrittenAsync(TestLaunch t, Guid launch, params Guid[] skus)
    {
        Assert.Equal(0, await HoldsAsync(t, launch));
        Assert.Equal(0, await ConsumedAsync(t, launch));
        await using var db = t.NewDb();
        Assert.Equal(0, await db.LimitUsage.CountAsync(u => u.LaunchId == launch, Ct));
        foreach (var sku in skus) Assert.Equal((0, 0), await SkuAsync(t, sku));
    }

    /// <summary>A token signed by hand under <paramref name="kid"/> with the host's key of that kid.</summary>
    private static string Signed(TestLaunch t, string kid, Guid account, Guid launch, long expMs)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { pid = Guid.NewGuid(), aid = account, lid = launch, exp = expMs, kid });
        var key = t.Services.GetRequiredService<LaunchOptions>().DecodedKeys[kid];
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));
    }

    private static long InMs(TimeSpan ttl) => DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Hold_moves_counters_and_consumes_the_pass()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var order = Guid.NewGuid();
        var pass = await Seed.PassAsync(t, launch.Id, account);
        Assert.True(await t.Redis.KeyExistsAsync(RedisKeys.Pass(launch.Id, account)));

        var response = await Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account, passId: pass), (launch.Skus[0], 2)));

        Assert.Equal((launch.Skus[0].ToString("D"), 2), Assert.Single(response.Lines, l => true) is var l ? (l.SkuId, l.Quantity) : default);
        await using var db = t.NewDb();
        var hold = await db.Holds.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Id(hold.Id), response.HoldId);
        Assert.Equal((order, launch.Id, account, pass, "held", 0L), (hold.OrderId, hold.LaunchId, hold.AccountId, hold.PassId, hold.Status, hold.Version));
        var line = await db.HoldLines.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((hold.Id, launch.Skus[0], launch.Products[0], 2), (line.HoldId, line.SkuId, line.ProductId, line.Quantity));
        var usage = await db.LimitUsage.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((account, launch.Products[0], 2, 0), (usage.AccountId, usage.ProductId, usage.Held, usage.Sold));
        Assert.Equal((2, 0), await SkuAsync(t, launch.Skus[0]));
        var consumed = await db.ConsumedPasses.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((pass, launch.Id, account, order), (consumed.PassId, consumed.LaunchId, consumed.AccountId, consumed.OrderId));
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.Pass(launch.Id, account)));
        Assert.Null(await t.Redis.SortedSetScoreAsync(RedisKeys.Passes(launch.Id), Id(pass)));
        Assert.Equal(1, metrics.Sum("holds_total", ("result", "ok"), ("launch", Id(launch.Id))));
        Assert.Equal(1, metrics.Count("hold_seconds"));
        Assert.Equal(98, metrics.Latest("allocation_available", ("launch", Id(launch.Id)), ("sku", Id(launch.Skus[0]))));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L01_1000_concurrent_holds_for_allocation_100()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(1, 100)]);
        var sku = launch.Skus[0];

        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(async _ =>
        {
            var account = Guid.NewGuid();
            try
            {
                await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (sku, 1)));
                return null;
            }
            catch (RpcException e)
            {
                return Rpc.Info(e);
            }
        }));

        Assert.Equal(100, results.Count(r => r is null));
        var rejected = results.Where(r => r is not null).ToList();
        Assert.Equal(900, rejected.Count);
        Assert.All(rejected, r => Assert.Equal((LaunchErrors.SoldOut, Id(sku)), (r!.Reason, r.Metadata["skuId"])));
        Assert.Equal(100, await HoldsAsync(t, launch.Id));
        Assert.Equal(100, await ConsumedAsync(t, launch.Id));
        Assert.Equal((100, 0), await SkuAsync(t, sku));
        Assert.Equal(0, t.ErrorLogs);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L02_limit_2_holds_2_then_1_more_with_a_new_pass_is_LIMIT_EXCEEDED()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 2)));

        using var metrics = new MetricCapture(t.Services);
        var info = await Rpc.FailsAsync(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1))), LaunchErrors.LimitExceeded);

        Assert.Equal((Id(launch.Products[0]), "0"), (info.Metadata["productId"], info.Metadata["remaining"]));
        Assert.Equal(1, await ConsumedAsync(t, launch.Id)); // the second pass is not used up
        Assert.Equal(1, await HoldsAsync(t, launch.Id));
        Assert.Equal((2, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal(1, metrics.Sum("holds_total", ("result", "limit_exceeded")));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L03_same_pass_for_two_holds_at_once()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);

        for (var round = 1; round <= 20; round++)
        {
            var account = Guid.NewGuid();
            var token = Holds.Token(t, launch.Id, account);
            var calls = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try
                {
                    await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (launch.Skus[0], 1)));
                    return null;
                }
                catch (RpcException e)
                {
                    return Rpc.Info(e);
                }
            }));

            Assert.Single(calls, c => c is null);
            Assert.Equal(LaunchErrors.PassAlreadyUsed, Assert.Single(calls, c => c is not null)!.Reason);
            Assert.Equal((round, 0), await SkuAsync(t, launch.Skus[0]));
        }
        Assert.Equal(20, await HoldsAsync(t, launch.Id));
        Assert.Equal(20, await ConsumedAsync(t, launch.Id));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L04_sold_out_then_same_pass_on_another_sku_succeeds()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(2, 0, 5)]);
        var account = Guid.NewGuid();
        var token = Holds.Token(t, launch.Id, account);

        var info = await Rpc.FailsAsync(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (launch.Skus[0], 1))), LaunchErrors.SoldOut);
        Assert.Equal(Id(launch.Skus[0]), info.Metadata["skuId"]);
        Assert.Equal(0, await ConsumedAsync(t, launch.Id)); // §8: the pass is still unused

        await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (launch.Skus[1], 1)));

        Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((1, 0), await SkuAsync(t, launch.Skus[1]));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L16_tampered_token_is_PASS_INVALID()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var token = Holds.Token(t, launch.Id, account);
        var parts = token.Split('.');
        var badSignature = parts[0] + "." + (parts[1][0] == 'A' ? 'B' : 'A') + parts[1][1..];
        var otherPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")).TrimEnd('=') + "." + parts[1];

        foreach (var bad in new[] { badSignature, otherPayload })
            await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), bad, (launch.Skus[0], 1))), LaunchErrors.PassInvalid);

        await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
        Assert.Equal(2, metrics.Sum("holds_total", ("result", "pass_invalid")));
    }

    [Fact]
    public async Task Garbage_tokens_are_PASS_INVALID()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var good = Holds.Token(t, launch.Id, account);
        var key = t.Services.GetRequiredService<LaunchOptions>().DecodedKeys["k1"];
        string Mac(byte[] payload, string kid = "k1") => Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));
        string Json(string json) => Mac(Encoding.UTF8.GetBytes(json));
        string Fields(string exp, string kid = "k1") => Json($$"""{"pid":"{{Guid.NewGuid()}}","aid":"{{account}}","lid":"{{launch.Id}}","exp":{{exp}},"kid":"{{kid}}"}""");

        var garbage = new[]
        {
            "", "abc", "a.b", ".", "a.b.c", "!!!.???", good.Split('.')[0], good.Split('.')[0] + ".", "." + good.Split('.')[1],
            Json("not json"), Json("[]"), Json("{}"), Json("""{"kid":"k1"}"""),
            Fields("\"soon\""), Fields("null"), Fields("1.5e400"), Fields("1000", "zz"), Fields("1000", "k9"),
            Mac([0xff, 0xfe, 0xfd]), Mac(Encoding.UTF8.GetBytes($$"""{"pid":"x","aid":"{{account}}","lid":"{{launch.Id}}","exp":1,"kid":"k1"}""")),
            new string('x', 5000),
        };
        foreach (var token in garbage)
            await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (launch.Skus[0], 1))), LaunchErrors.PassInvalid);

        Assert.Equal(0, t.ErrorLogs);
        await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
    }

    [Fact]
    public async Task Token_of_another_account_or_unknown_launch_is_PASS_INVALID()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();

        await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, Guid.NewGuid()), (launch.Skus[0], 1))), LaunchErrors.PassInvalid);
        await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, Guid.NewGuid(), account), (launch.Skus[0], 1))), LaunchErrors.PassInvalid);

        await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
    }

    [Fact]
    public async Task Token_signed_with_the_previous_key_is_accepted()
    {
        await using var t = await StartAsync(); // PassKeys k1 (current) and k0
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();

        var response = await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Signed(t, "k0", account, launch.Id, InMs(TimeSpan.FromMinutes(10))), (launch.Skus[0], 1)));

        Assert.NotEmpty(response.HoldId);
    }

    [Fact]
    public async Task Expired_pass_is_PASS_EXPIRED()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();

        await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account, TimeSpan.FromSeconds(-1)), (launch.Skus[0], 1))), LaunchErrors.PassExpired);

        await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
        Assert.Equal(1, metrics.Sum("holds_total", ("result", "pass_expired")));
    }

    [Fact]
    public async Task Retry_with_the_same_lines_returns_the_same_hold()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(5, 10, 10)]);
        var account = Guid.NewGuid();
        var order = Guid.NewGuid();
        var token = Holds.Token(t, launch.Id, account);
        var (a, b) = (launch.Skus[0], launch.Skus[1]);

        var first = await Holds.CallAsync(t, account, Holds.Request(order, token, (a, 2), (b, 1)));
        var retry = await Holds.CallAsync(t, account, Holds.Request(order, token, (b, 1), (a, 2))); // another order of the lines

        Assert.Equal(first.HoldId, retry.HoldId);
        Assert.Equal(first.Lines.Select(l => (l.SkuId, l.Quantity)), retry.Lines.Select(l => (l.SkuId, l.Quantity)));
        Assert.Equal(new[] { a, b }.Select(Id).Order(StringComparer.Ordinal), first.Lines.Select(l => l.SkuId)); // ordered by sku_id
        Assert.Equal(1, await HoldsAsync(t, launch.Id));
        Assert.Equal(1, await ConsumedAsync(t, launch.Id));
        Assert.Equal((2, 0), await SkuAsync(t, a));
        Assert.Equal((1, 0), await SkuAsync(t, b));
        Assert.Equal(2, metrics.Sum("holds_total", ("result", "ok")));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Retry_after_the_pass_expired_is_PASS_EXPIRED()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var order = Guid.NewGuid();
        var pass = Guid.NewGuid();
        await Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account, passId: pass), (launch.Skus[0], 1)));

        // The hold exists, but the same pass is past its exp (spec §7).
        await Fails(Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account, TimeSpan.FromSeconds(-1), pass), (launch.Skus[0], 1))), LaunchErrors.PassExpired);

        Assert.Equal(1, await HoldsAsync(t, launch.Id));
    }

    [Fact]
    public async Task Same_order_with_other_lines_is_HOLD_CONFLICT()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(5, 10, 10)]);
        var account = Guid.NewGuid();
        var order = Guid.NewGuid();
        await Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account), (launch.Skus[0], 1)));

        foreach (var other in new[] { new[] { (launch.Skus[0], 2) }, [(launch.Skus[1], 1)], [(launch.Skus[0], 1), (launch.Skus[1], 1)] })
            await Fails(Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account), other)), LaunchErrors.HoldConflict);

        Assert.Equal(1, await ConsumedAsync(t, launch.Id)); // none of the other passes is used up
        Assert.Equal((1, 0), await SkuAsync(t, launch.Skus[0]));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Concurrent_retry_of_one_order_returns_one_hold()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);

        for (var round = 1; round <= 10; round++)
        {
            var account = Guid.NewGuid();
            var request = Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1));

            var both = await Task.WhenAll(Holds.CallAsync(t, account, request), Holds.CallAsync(t, account, request));

            Assert.Equal(both[0].HoldId, both[1].HoldId);
            Assert.Equal((round, 0), await SkuAsync(t, launch.Skus[0]));
        }
        Assert.Equal(10, await HoldsAsync(t, launch.Id));
        Assert.Equal(10, await ConsumedAsync(t, launch.Id));
        Assert.Equal(0, t.ErrorLogs);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Hold_outside_preorder_open_is_LAUNCH_NOT_OPEN()
    {
        await using var t = await StartAsync();
        var announced = await Seed.LaunchAsync(t, LaunchStages.Announced);
        var released = await Seed.LaunchAsync(t, LaunchStages.Released);
        var closed = await Seed.LaunchAsync(t);
        await t.SqlAsync($"UPDATE launches SET waiting_room_closed_at = now() WHERE id = {closed.Id}");
        var account = Guid.NewGuid();

        foreach (var launch in new[] { announced, released, closed })
            await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1))), LaunchErrors.LaunchNotOpen);

        foreach (var launch in new[] { announced, released, closed }) await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
    }

    [Fact]
    public async Task Sku_of_another_launch_is_SKU_NOT_IN_LAUNCH()
    {
        await using var t = await StartAsync();
        var a = await Seed.LaunchAsync(t);
        var b = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var token = Holds.Token(t, a.Id, account);

        await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (b.Skus[0], 1))), LaunchErrors.SkuNotInLaunch);
        await Fails(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (a.Skus[0], 1), (Guid.NewGuid(), 1))), LaunchErrors.SkuNotInLaunch);

        await NothingWrittenAsync(t, a.Id, a.Skus[0]);
        await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (a.Skus[0], 1))); // the pass is unused
    }

    [Fact]
    public async Task Huge_quantities_fail_cleanly()
    {
        await using var t = await StartAsync();
        var account = Guid.NewGuid();

        // One line of int.MaxValue against limit 3.
        var one = await Seed.LaunchAsync(t, products: [new SeedProduct(3, 100)]);
        var info = await Rpc.FailsAsync(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, one.Id, account), (one.Skus[0], int.MaxValue))), LaunchErrors.LimitExceeded);
        Assert.Equal((Id(one.Products[0]), "3"), (info.Metadata["productId"], info.Metadata["remaining"]));

        // Two SKUs of one product at 2^30: the sum is 2^31, which an int cannot hold.
        var two = await Seed.LaunchAsync(t, products: [new SeedProduct(5, 10, 10)]);
        info = await Rpc.FailsAsync(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, two.Id, account), (two.Skus[0], 1 << 30), (two.Skus[1], 1 << 30))), LaunchErrors.LimitExceeded);
        Assert.Equal((Id(two.Products[0]), "5"), (info.Metadata["productId"], info.Metadata["remaining"]));

        // A limit of int.MaxValue is no obstacle; the allocation of 10 is.
        var three = await Seed.LaunchAsync(t, products: [new SeedProduct(int.MaxValue, 10)]);
        info = await Rpc.FailsAsync(Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, three.Id, account), (three.Skus[0], int.MaxValue))), LaunchErrors.SoldOut);
        Assert.Equal(Id(three.Skus[0]), info.Metadata["skuId"]);

        foreach (var launch in new[] { one, two, three }) await NothingWrittenAsync(t, launch.Id, [.. launch.Skus]);
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Validation_rules()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var token = Holds.Token(t, launch.Id, account);
        var sku = launch.Skus[0];

        var cases = new (HoldAllocationRequest Request, string Field)[]
        {
            (Holds.Request(Guid.NewGuid(), token, (sku, 1)) is var r1 && Set(r1, x => x.OrderId = "x") ? r1 : null!, "order_id"),
            (Holds.Request(Guid.NewGuid(), token), "lines"),
            (Holds.Request(Guid.NewGuid(), token, (sku, 1)) is var r2 && Set(r2, x => x.Lines[0].SkuId = "x") ? r2 : null!, "lines.sku_id"),
            (Holds.Request(Guid.NewGuid(), token, (sku, 1), (sku, 2)), "lines.sku_id"),
            (Holds.Request(Guid.NewGuid(), token, (sku, 0)), "lines.quantity"),
            (Holds.Request(Guid.NewGuid(), token, (sku, -3)), "lines.quantity"),
        };
        foreach (var (request, field) in cases)
        {
            var info = await Rpc.FailsAsync(Holds.CallAsync(t, account, request), LaunchErrors.ValidationFailed);
            Assert.Equal(field, info.Metadata["field"]);
        }

        await NothingWrittenAsync(t, launch.Id, sku);
        await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), token, (sku, 1))); // the pass is unused

        static bool Set(HoldAllocationRequest r, Action<HoldAllocationRequest> change)
        {
            change(r);
            return true;
        }
    }

    [Fact]
    public async Task Staff_caller_is_ROLE_NOT_ALLOWED()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var request = Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1));

        await Rpc.FailsAsync(t.Internal.HoldAllocationAsync(request, Api.Staff("admin"), cancellationToken: Ct), BloomErrors.RoleNotAllowed);

        await NothingWrittenAsync(t, launch.Id, launch.Skus[0]);
    }

    [Fact]
    public async Task Raising_allocation_while_holds_run_keeps_the_invariant()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: [new SeedProduct(1, 50)]);
        var sku = launch.Skus[0];

        var holds = Enumerable.Range(0, 100).Select(async i =>
        {
            if (i == 20) await Task.Delay(5, Ct);
            var account = Guid.NewGuid();
            try
            {
                await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (sku, 1)));
                return null;
            }
            catch (RpcException e)
            {
                return Rpc.Info(e);
            }
        }).ToList();
        var raise = Task.Run(async () =>
        {
            await Task.Delay(10, Ct);
            await t.Admin.SetAllocationAsync(new() { LaunchId = Id(launch.Id), SkuId = Id(sku), Allocation = 80, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        }, Ct);

        var results = await Task.WhenAll(holds);
        await raise;

        var ok = results.Count(r => r is null);
        Assert.InRange(ok, 50, 80);
        Assert.All(results.Where(r => r is not null), r => Assert.Equal(LaunchErrors.SoldOut, r!.Reason));
        Assert.Equal((ok, 0), await SkuAsync(t, sku));
        Assert.Equal(0, t.ErrorLogs);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Hold_succeeds_with_redis_down()
    {
        await using var t = await TestLaunch.StartAsync(containers, o => o.Config["Launch:Redis"] = "127.0.0.1:1,connectTimeout=500");
        var launch = await Seed.LaunchAsync(t);
        var account = Guid.NewGuid();
        var clock = Stopwatch.StartNew();

        var response = await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1)));

        Assert.NotEmpty(response.HoldId);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Warning]", StringComparison.Ordinal));
        Assert.Equal(0, t.ErrorLogs);
        Assert.Equal((1, 0), await SkuAsync(t, launch.Skus[0]));
    }
}
