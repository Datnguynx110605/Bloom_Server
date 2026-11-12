using Bloom.Identity.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.DeadLetters;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Tests;

public class AdminEditTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync() => TestLaunch.StartAsync(containers);

    // Seed computes times from the app clock; these checks compare with the database clock (Task 4 ruling M2), so tests put the boundary values on the database axis.
    private static async Task<DateTimeOffset> DbNowAsync(TestLaunch t)
    {
        await using var db = t.NewDb();
        return await db.NowAsync(Ct);
    }

    private static Timestamp At(DateTimeOffset value) => Timestamp.FromDateTimeOffset(value);

    private static Task SetTimesAsync(TestLaunch t, Guid launch, TimeSpan launchIn, TimeSpan releaseIn) =>
        t.SqlAsync($"UPDATE launches SET launch_time = now() + {launchIn}, release_date = now() + {releaseIn} WHERE id = {launch}");

    private static async Task<int> SkuAllocationAsync(TestLaunch t, Guid sku)
    {
        await using var db = t.NewDb();
        return await db.LaunchSkus.AsNoTracking().Where(s => s.SkuId == sku).Select(s => s.Allocation).SingleAsync(Ct);
    }

    private static async Task<int> StageEventsAsync(TestLaunch t) => (await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged)).Count;

    private static async Task NothingChangedAsync(TestLaunch t, Guid launch, long version = 0)
    {
        Assert.Equal(version, (await t.RowAsync(launch)).Version);
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded));
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged));
    }

    private static async Task FieldFailsAsync<T>(AsyncUnaryCall<T> call, string field)
    {
        var info = await Rpc.FailsAsync(call, LaunchErrors.ValidationFailed);
        Assert.Equal(field, info.Metadata["field"]);
    }

    [Fact]
    public async Task L4_admin_view_read_in_one_snapshot()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(2, 100));
        var account = Guid.NewGuid();
        // A hold commits after the view has read the launch and its products, before it reads the SKUs.
        t.Commands.AfterReader = async command =>
        {
            if (!command.CommandText.Contains("FROM launch_products", StringComparison.Ordinal)) return;
            t.Commands.AfterReader = null;
            await Holds.CallAsync(t, account, Holds.Request(Guid.NewGuid(), Holds.Token(t, launch.Id, account), (launch.Skus[0], 1)));
        };

        var view = await t.Admin.SetAllocationAsync(new() { LaunchId = launch.Id.ToString(), SkuId = launch.Skus[0].ToString(), Allocation = 110, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Null(t.Commands.AfterReader); // the hold did run between the reads, and it committed
        Assert.Equal((1, 0), await Holds.SkuAsync(t, launch.Skus[0]));
        var sku = view.Skus.Single();
        Assert.Equal((1L, 110, 0, 0), (view.Version, sku.Allocation, sku.Held, sku.Sold)); // all of it from before the hold
    }

    [Fact]
    public async Task Each_edit_bumps_version_audits_and_returns_the_admin_view()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2, 100));
        var (product, sku) = (launch.Products[0], launch.Skus[0]);
        var now = await DbNowAsync(t);
        var l = launch.Id.ToString();

        var view = await t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = sku.ToString(), Allocation = 40, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(1, view.Version);
        Assert.Equal(40, view.Skus.Single().Allocation);
        var allocation = await Outbox.AuditAsync(t, "launch.allocation-changed");
        Assert.Equal(("launch", l), (allocation.TargetType, allocation.TargetId));
        Assert.Equal((100, 40, sku.ToString()), (allocation.Details.GetProperty("before").GetInt32(), allocation.Details.GetProperty("after").GetInt32(), allocation.Details.GetProperty("skuId").GetString()));

        view = await t.Admin.SetPurchaseLimitAsync(new() { LaunchId = l, ProductId = product.ToString(), PurchaseLimit = 5, Version = 1 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal((2, 5), (view.Version, view.Products.Single().PurchaseLimit));
        var limit = await Outbox.AuditAsync(t, "launch.purchase-limit-changed");
        Assert.Equal((2, 5, product.ToString()), (limit.Details.GetProperty("before").GetInt32(), limit.Details.GetProperty("after").GetInt32(), limit.Details.GetProperty("productId").GetString()));

        var release = now.AddDays(30);
        view = await t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(release), Version = 2 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(3, view.Version);
        Assert.Equal(release, view.ReleaseDate.ToDateTimeOffset());
        await Outbox.AuditAsync(t, "launch.release-date-changed");

        var launchTime = now.AddDays(2);
        view = await t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, LaunchTime = At(launchTime), Version = 3 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(4, view.Version);
        Assert.Equal(launchTime, view.LaunchTime.ToDateTimeOffset());
        await Outbox.AuditAsync(t, "launch.launch-time-changed");

        view = await t.Admin.SetAdmissionRateAsync(new() { LaunchId = l, AdmissionRate = 77, Version = 4 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal((5, 77), (view.Version, view.AdmissionRate));
        var rate = await Outbox.AuditAsync(t, "launch.admission-rate-changed");
        Assert.Equal((200, 77), (rate.Details.GetProperty("before").GetInt32(), rate.Details.GetProperty("after").GetInt32()));

        view = await t.Admin.PublishLaunchAsync(new() { LaunchId = l, Version = 5 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal((6, "announced"), (view.Version, view.Stage));
        await Outbox.AuditAsync(t, "launch.published");

        var row = await t.RowAsync(launch.Id);
        Assert.Equal(6, row.Version);
        Assert.True(row.UpdatedAt > row.CreatedAt);
        Assert.Equal(row.UpdatedAt, view.UpdatedAt.ToDateTimeOffset());
    }

    [Fact]
    public async Task Stale_version_is_CONCURRENCY_CONFLICT_before_the_stage_rule()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t); // preorder_open; every edit but the rate has a stage rule that would fail
        var released = await Seed.LaunchAsync(t, LaunchStages.Released);
        var calls = new Func<Guid, SeededLaunch, AsyncUnaryCall<AdminLaunch>>[]
        {
            (id, s) => t.Admin.SetAllocationAsync(new() { LaunchId = id.ToString(), SkuId = s.Skus[0].ToString(), Allocation = 1, Version = 9 }, Api.Staff(), cancellationToken: Ct),
            (id, s) => t.Admin.SetPurchaseLimitAsync(new() { LaunchId = id.ToString(), ProductId = s.Products[0].ToString(), PurchaseLimit = 1, Version = 9 }, Api.Staff(), cancellationToken: Ct),
            (id, _) => t.Admin.SetReleaseDateAsync(new() { LaunchId = id.ToString(), ReleaseDate = At(DateTimeOffset.UtcNow.AddYears(1)), Version = 9 }, Api.Staff(), cancellationToken: Ct),
            (id, _) => t.Admin.SetLaunchTimeAsync(new() { LaunchId = id.ToString(), LaunchTime = At(DateTimeOffset.UtcNow), Version = 9 }, Api.Staff(), cancellationToken: Ct),
            (id, _) => t.Admin.SetAdmissionRateAsync(new() { LaunchId = id.ToString(), AdmissionRate = 5, Version = 9 }, Api.Staff(), cancellationToken: Ct),
            (id, _) => t.Admin.PublishLaunchAsync(new() { LaunchId = id.ToString(), Version = 9 }, Api.Staff(), cancellationToken: Ct),
        };

        foreach (var call in calls)
        foreach (var target in new[] { launch, released })
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => call(target.Id, target).ResponseAsync);
            Assert.Equal(StatusCode.Aborted, e.StatusCode);
            Assert.Equal("CONCURRENCY_CONFLICT", Rpc.Info(e).Reason);
        }

        await Rpc.FailsAsync(t.Admin.SetAdmissionRateAsync(new() { LaunchId = Guid.NewGuid().ToString(), AdmissionRate = 5, Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.NotFoundReason);
        await NothingChangedAsync(t, launch.Id);
        Assert.Equal(0, (await t.RowAsync(released.Id)).Version);
    }

    [Fact]
    public async Task Validation_of_ids_and_values_comes_before_any_change()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var l = launch.Id.ToString();
        var staff = Api.Staff();

        await FieldFailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = "x", SkuId = launch.Skus[0].ToString(), Version = 0 }, staff, cancellationToken: Ct), "launch_id");
        await FieldFailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = "x", Version = 0 }, staff, cancellationToken: Ct), "sku_id");
        await FieldFailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = launch.Skus[0].ToString(), Allocation = -1, Version = 0 }, staff, cancellationToken: Ct), "allocation");
        await Rpc.FailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = Guid.NewGuid().ToString(), Allocation = 1, Version = 0 }, staff, cancellationToken: Ct), LaunchErrors.NotFoundReason);
        await FieldFailsAsync(t.Admin.SetPurchaseLimitAsync(new() { LaunchId = l, ProductId = "x", PurchaseLimit = 1, Version = 0 }, staff, cancellationToken: Ct), "product_id");
        await FieldFailsAsync(t.Admin.SetPurchaseLimitAsync(new() { LaunchId = l, ProductId = launch.Products[0].ToString(), PurchaseLimit = 0, Version = 0 }, staff, cancellationToken: Ct), "purchase_limit");
        await Rpc.FailsAsync(t.Admin.SetPurchaseLimitAsync(new() { LaunchId = l, ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 1, Version = 0 }, staff, cancellationToken: Ct), LaunchErrors.NotFoundReason);
        await FieldFailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, Version = 0 }, staff, cancellationToken: Ct), "release_date");
        await FieldFailsAsync(t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, Version = 0 }, staff, cancellationToken: Ct), "launch_time");
        await FieldFailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = "x", Version = 0 }, staff, cancellationToken: Ct), "launch_id");

        await NothingChangedAsync(t, launch.Id);
    }

    [Fact]
    public async Task Allocation_in_draft_can_go_down()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        using var metrics = new MetricCapture(t.Services);

        await t.Admin.SetAllocationAsync(new() { LaunchId = launch.Id.ToString(), SkuId = launch.Skus[0].ToString(), Allocation = 0, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(0, await SkuAllocationAsync(t, launch.Skus[0]));
        Assert.Equal(0, metrics.Latest("allocation_available", ("launch", launch.Id.ToString()), ("sku", launch.Skus[0].ToString())));
        Assert.Equal(0, await StageEventsAsync(t));
    }

    [Theory]
    [InlineData(LaunchStages.Announced)]
    [InlineData(LaunchStages.PreorderOpen)]
    public async Task L13_lower_allocation_while_announced_is_ALLOCATION_DECREASE_NOT_ALLOWED(string stage)
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, stage);
        var (l, sku) = (launch.Id.ToString(), launch.Skus[0]);
        await t.SqlAsync($"UPDATE launch_skus SET held = 3, sold = 4 WHERE sku_id = {sku}");
        using var metrics = new MetricCapture(t.Services);

        await Rpc.FailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = sku.ToString(), Allocation = 99, Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.AllocationDecreaseNotAllowed);
        Assert.Equal((100, 0L), (await SkuAllocationAsync(t, sku), (await t.RowAsync(launch.Id)).Version));

        await t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = sku.ToString(), Allocation = 100, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync; // equal is allowed
        await t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = sku.ToString(), Allocation = 150, Version = 1 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(150, await SkuAllocationAsync(t, sku));
        Assert.Equal(143, metrics.Latest("allocation_available", ("launch", l), ("sku", sku.ToString())));
        Assert.Equal(2, (await t.RowAsync(launch.Id)).Version);
    }

    [Fact]
    public async Task Allocation_in_released_is_INVALID_STAGE()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Released);

        await Rpc.FailsAsync(t.Admin.SetAllocationAsync(new() { LaunchId = launch.Id.ToString(), SkuId = launch.Skus[0].ToString(), Allocation = 500, Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);

        Assert.Equal(100, await SkuAllocationAsync(t, launch.Skus[0]));
        await NothingChangedAsync(t, launch.Id);
    }

    [Theory]
    [InlineData(LaunchStages.PreorderOpen)]
    [InlineData(LaunchStages.Released)]
    public async Task L14_change_limit_while_preorder_open_is_PURCHASE_LIMIT_LOCKED(string stage)
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, stage);

        await Rpc.FailsAsync(t.Admin.SetPurchaseLimitAsync(new() { LaunchId = launch.Id.ToString(), ProductId = launch.Products[0].ToString(), PurchaseLimit = 9, Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.PurchaseLimitLocked);

        await using var db = t.NewDb();
        Assert.Equal(2, await db.LaunchProducts.AsNoTracking().Select(p => p.PurchaseLimit).SingleAsync(Ct));
        await NothingChangedAsync(t, launch.Id);
    }

    [Fact]
    public async Task Limit_and_release_date_changes_from_announced_publish_stage_changed()
    {
        await using var t = await StartAsync();
        var announced = await Seed.LaunchAsync(t, LaunchStages.Announced);
        var draft = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var open = await Seed.LaunchAsync(t);
        var release = (await DbNowAsync(t)).AddDays(40);

        await t.Admin.SetPurchaseLimitAsync(new() { LaunchId = announced.Id.ToString(), ProductId = announced.Products[0].ToString(), PurchaseLimit = 6, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        var events = await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged);
        var first = Assert.Single(events);
        Assert.Equal((announced.Id, "announced", 6), (first.LaunchId, first.Stage, first.Products.Single().PurchaseLimit));
        Assert.Equal(announced.Skus[0], first.Skus.Single().SkuId);

        await t.Admin.SetReleaseDateAsync(new() { LaunchId = announced.Id.ToString(), ReleaseDate = At(release), Version = 1 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        events = await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged);
        Assert.Equal(2, events.Count);
        Assert.Equal(release, events[1].ReleaseDate);

        // Plan R10 / spec §7: "from announced on", so a release date change in preorder_open publishes too.
        await t.Admin.SetReleaseDateAsync(new() { LaunchId = open.Id.ToString(), ReleaseDate = At(release), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(3, await StageEventsAsync(t));

        // The same changes in draft publish nothing.
        await t.Admin.SetPurchaseLimitAsync(new() { LaunchId = draft.Id.ToString(), ProductId = draft.Products[0].ToString(), PurchaseLimit = 6, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        await t.Admin.SetReleaseDateAsync(new() { LaunchId = draft.Id.ToString(), ReleaseDate = At(release), Version = 1 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(3, await StageEventsAsync(t));
    }

    [Fact]
    public async Task Release_date_rules()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced);
        await SetTimesAsync(t, launch.Id, TimeSpan.FromHours(1), TimeSpan.FromDays(7));
        var row = await t.RowAsync(launch.Id);
        var now = await DbNowAsync(t);
        var l = launch.Id.ToString();

        await FieldFailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(now.AddMinutes(-1)), Version = 0 }, Api.Staff(), cancellationToken: Ct), "release_date"); // <= now()
        await FieldFailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(row.LaunchTime), Version = 0 }, Api.Staff(), cancellationToken: Ct), "release_date"); // <= launch_time
        await FieldFailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(row.LaunchTime.AddMinutes(-5)), Version = 0 }, Api.Staff(), cancellationToken: Ct), "release_date");
        Assert.Equal(row.ReleaseDate, (await t.RowAsync(launch.Id)).ReleaseDate);
        Assert.Equal(0, (await t.RowAsync(launch.Id)).Version);

        var ok = await t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(row.LaunchTime.AddMilliseconds(1)), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(1, ok.Version);

        var released = await Seed.LaunchAsync(t, LaunchStages.Released);
        await Rpc.FailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = released.Id.ToString(), ReleaseDate = At(now.AddYears(1)), Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);
    }

    [Fact]
    public async Task Release_date_must_be_after_the_database_now_even_when_after_launch_time()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced);
        await SetTimesAsync(t, launch.Id, TimeSpan.FromHours(-2), TimeSpan.FromDays(7)); // launch_time in the past
        var now = await DbNowAsync(t);
        var l = launch.Id.ToString();

        // after launch_time, before now(): only the "> now()" rule can reject it
        await FieldFailsAsync(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(now.AddHours(-1)), Version = 0 }, Api.Staff(), cancellationToken: Ct), "release_date");
        Assert.Equal(0, (await t.RowAsync(launch.Id)).Version);

        var ok = await t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(now.AddHours(1)), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(1, ok.Version);
    }

    [Theory]
    [InlineData(LaunchStages.Announced)]
    [InlineData(LaunchStages.PreorderOpen)]
    public async Task L20_change_launch_time_while_announced_is_INVALID_STAGE(string stage)
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, stage);

        await Rpc.FailsAsync(t.Admin.SetLaunchTimeAsync(new() { LaunchId = launch.Id.ToString(), LaunchTime = At((await DbNowAsync(t)).AddHours(2)), Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);

        await NothingChangedAsync(t, launch.Id);
    }

    [Fact]
    public async Task Launch_time_in_draft_must_be_before_the_release_date()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var release = (await t.RowAsync(launch.Id)).ReleaseDate;
        var l = launch.Id.ToString();

        await FieldFailsAsync(t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, LaunchTime = At(release), Version = 0 }, Api.Staff(), cancellationToken: Ct), "launch_time");
        await FieldFailsAsync(t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, LaunchTime = At(release.AddDays(1)), Version = 0 }, Api.Staff(), cancellationToken: Ct), "launch_time");
        Assert.Equal(0, (await t.RowAsync(launch.Id)).Version);

        var view = await t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, LaunchTime = At(release.AddMilliseconds(-1)), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal(1, view.Version);
        Assert.Equal(0, await StageEventsAsync(t));
    }

    [Fact]
    public async Task L22_admission_rate_above_the_ceiling_is_rejected()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t); // preorder_open: "mid-launch"
        var l = launch.Id.ToString();

        await FieldFailsAsync(t.Admin.SetAdmissionRateAsync(new() { LaunchId = l, AdmissionRate = 201, Version = 0 }, Api.Staff(), cancellationToken: Ct), "admission_rate");
        await FieldFailsAsync(t.Admin.SetAdmissionRateAsync(new() { LaunchId = l, AdmissionRate = 0, Version = 0 }, Api.Staff(), cancellationToken: Ct), "admission_rate");
        Assert.Equal(0, (await t.RowAsync(launch.Id)).Version);

        var view = await t.Admin.SetAdmissionRateAsync(new() { LaunchId = l, AdmissionRate = 200, Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        Assert.Equal((1, 200), (view.Version, view.AdmissionRate));
        Assert.Equal(0, await StageEventsAsync(t));

        var released = await Seed.LaunchAsync(t, LaunchStages.Released);
        await Rpc.FailsAsync(t.Admin.SetAdmissionRateAsync(new() { LaunchId = released.Id.ToString(), AdmissionRate = 5, Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);
    }

    [Fact]
    public async Task Publish_moves_draft_to_announced_and_publishes()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: [new SeedProduct(2, 10, 20), new SeedProduct(3, 5)]);
        await SetTimesAsync(t, launch.Id, TimeSpan.FromHours(1), TimeSpan.FromDays(7));
        await t.Redis.StringSetAsync(RedisKeys.State(launch.Id), "stale", TimeSpan.FromMinutes(1));

        var view = await t.Admin.PublishLaunchAsync(new() { LaunchId = launch.Id.ToString(), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(("announced", 1), (view.Stage, (int)view.Version));
        Assert.Equal("announced", (await t.RowAsync(launch.Id)).Stage);
        var e = Assert.Single(await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged));
        Assert.Equal((launch.Id, "announced"), (e.LaunchId, e.Stage));
        Assert.Equal(launch.Products.Order(), e.Products.Select(p => p.ProductId).Order());
        Assert.Equal(launch.Skus.Order(), e.Skus.Select(s => s.SkuId).Order());
        Assert.Equal((await t.RowAsync(launch.Id)).LaunchTime, e.LaunchTime);
        var audit = await Outbox.AuditAsync(t, "launch.published");
        Assert.Equal(launch.Id.ToString(), audit.TargetId);
        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.State(launch.Id)));
    }

    [Fact]
    public async Task Publish_rules()
    {
        await using var t = await StartAsync();
        var noSku = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        var oneEmpty = await Seed.LaunchAsync(t, LaunchStages.Draft, products: [new SeedProduct(2, 10), new SeedProduct(2)]);
        var late = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var announced = await Seed.LaunchAsync(t, LaunchStages.Announced);
        var empty = await t.Admin.CreateLaunchAsync(new() { Name = "Empty", LaunchTime = At((await DbNowAsync(t)).AddDays(1)), ReleaseDate = At((await DbNowAsync(t)).AddDays(2)), AdmissionRate = 5 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        foreach (var id in new[] { noSku.Id, oneEmpty.Id, announced.Id }) await SetTimesAsync(t, id, TimeSpan.FromHours(1), TimeSpan.FromDays(7));
        await SetTimesAsync(t, late.Id, TimeSpan.FromMinutes(-1), TimeSpan.FromDays(7));
        var baseline = (await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded)).Count; // the create above

        await FieldFailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = empty.Id, Version = 0 }, Api.Staff(), cancellationToken: Ct), "skus"); // no product at all
        await FieldFailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = noSku.Id.ToString(), Version = 0 }, Api.Staff(), cancellationToken: Ct), "skus");
        var info = await Rpc.FailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = oneEmpty.Id.ToString(), Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.ValidationFailed);
        Assert.Equal("skus", info.Metadata["field"]);
        await FieldFailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = late.Id.ToString(), Version = 0 }, Api.Staff(), cancellationToken: Ct), "launch_time");
        await Rpc.FailsAsync(t.Admin.PublishLaunchAsync(new() { LaunchId = announced.Id.ToString(), Version = 0 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);

        foreach (var id in new[] { noSku.Id, oneEmpty.Id, late.Id, announced.Id }) Assert.Equal(0, (await t.RowAsync(id)).Version);
        Assert.Equal("announced", (await t.RowAsync(announced.Id)).Stage);
        foreach (var id in new[] { noSku.Id, oneEmpty.Id, late.Id }) Assert.Equal("draft", (await t.RowAsync(id)).Stage);
        Assert.Equal(baseline, (await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded)).Count);
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged));
    }

    [Fact]
    public async Task Other_roles_are_ROLE_NOT_ALLOWED()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var l = launch.Id.ToString();
        var caller = Api.Staff("support");

        async Task Denied<T>(AsyncUnaryCall<T> call)
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
        }

        await Denied(t.Admin.SetAllocationAsync(new() { LaunchId = l, SkuId = launch.Skus[0].ToString(), Allocation = 1, Version = 0 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.SetPurchaseLimitAsync(new() { LaunchId = l, ProductId = launch.Products[0].ToString(), PurchaseLimit = 1, Version = 0 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.SetReleaseDateAsync(new() { LaunchId = l, ReleaseDate = At(DateTimeOffset.UtcNow.AddYears(1)), Version = 0 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.SetLaunchTimeAsync(new() { LaunchId = l, LaunchTime = At(DateTimeOffset.UtcNow), Version = 0 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.SetAdmissionRateAsync(new() { LaunchId = l, AdmissionRate = 5, Version = 0 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.PublishLaunchAsync(new() { LaunchId = l, Version = 0 }, caller, cancellationToken: Ct));
        await NothingChangedAsync(t, launch.Id);
    }
}
