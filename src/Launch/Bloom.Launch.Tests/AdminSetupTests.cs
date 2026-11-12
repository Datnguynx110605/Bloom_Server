using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Errors;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Tests;

public class AdminSetupTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync(Action<TestLaunchOptions>? configure = null) => TestLaunch.StartAsync(containers, configure);

    private static Timestamp In(TimeSpan span) => Timestamp.FromDateTimeOffset(new DateTimeOffset(DateTime.UtcNow.Add(span), TimeSpan.Zero));

    private static CreateLaunchRequest NewLaunch(string name = "Drop", int rate = 50) =>
        new() { Name = name, LaunchTime = In(TimeSpan.FromDays(10)), ReleaseDate = In(TimeSpan.FromDays(20)), AdmissionRate = rate };

    private static HttpRequestMessage Request(HttpMethod method, string url, string role = "catalog_manager", object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) }.WithCaller(Guid.NewGuid(), "staff", role);
        return request;
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Json)> SendAsync(TestLaunch t, HttpRequestMessage request)
    {
        var response = await t.Http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return (response, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<long> VersionAsync(TestLaunch t, Guid launch)
    {
        await using var db = t.NewDb();
        return await db.Launches.AsNoTracking().Where(l => l.Id == launch).Select(l => l.Version).SingleAsync(Ct);
    }

    private static async Task<string?> FieldFailsAsync<T>(AsyncUnaryCall<T> call, string field)
    {
        var info = await Rpc.FailsAsync(call, LaunchErrors.ValidationFailed);
        Assert.Equal(field, info.Metadata["field"]);
        return info.Metadata["field"];
    }

    [Fact]
    public async Task Create_returns_a_draft_admin_view()
    {
        await using var t = await StartAsync();
        var actor = Guid.NewGuid();
        var body = new { name = "  Spring drop ", launchTime = "2030-05-01T10:00:00Z", releaseDate = "2030-05-15T10:00:00Z", admissionRate = 50 };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/launches") { Content = JsonContent.Create(body) }.WithCaller(actor, "staff", "admin");

        var (_, json) = await SendAsync(t, request);

        var id = Guid.Parse(json.GetProperty("id").GetString()!);
        Assert.Equal("Spring drop", json.GetProperty("name").GetString());
        Assert.Equal("draft", json.GetProperty("stage").GetString());
        Assert.Equal("0", json.GetProperty("version").GetString());
        Assert.Equal(50, json.GetProperty("admissionRate").GetInt32());
        Assert.Equal(DateTimeOffset.Parse("2030-05-01T10:00:00Z"), json.GetProperty("launchTime").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2030-05-15T10:00:00Z"), json.GetProperty("releaseDate").GetDateTimeOffset());
        Assert.NotEqual(default, json.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(json.GetProperty("createdAt").GetDateTimeOffset(), json.GetProperty("updatedAt").GetDateTimeOffset());

        await using var db = t.NewDb();
        var row = await db.Launches.AsNoTracking().SingleAsync(l => l.Id == id, Ct);
        Assert.Equal(("draft", 0L, "Spring drop"), (row.Stage, row.Version, row.Name));
        var audit = await Outbox.AuditAsync(t, "launch.created");
        Assert.Equal((actor, "admin", "launch", "launch", id.ToString()), (audit.ActorId, audit.ActorRole, audit.Service, audit.TargetType, audit.TargetId));
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged));
    }

    [Fact]
    public async Task Create_validation()
    {
        await using var t = await StartAsync();
        var staff = Api.Staff();

        var emptyName = NewLaunch("   ");
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(emptyName, staff, cancellationToken: Ct), "name");
        var noLaunchTime = NewLaunch();
        noLaunchTime.LaunchTime = null;
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(noLaunchTime, staff, cancellationToken: Ct), "launch_time");
        var noRelease = NewLaunch();
        noRelease.ReleaseDate = null;
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(noRelease, staff, cancellationToken: Ct), "release_date");
        var same = NewLaunch();
        same.ReleaseDate = same.LaunchTime;
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(same, staff, cancellationToken: Ct), "release_date");
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(NewLaunch(rate: 0), staff, cancellationToken: Ct), "admission_rate");
        await FieldFailsAsync(t.Admin.CreateLaunchAsync(NewLaunch(rate: 201), staff, cancellationToken: Ct), "admission_rate"); // L-22 on create
        Assert.Equal(200, (await t.Admin.CreateLaunchAsync(NewLaunch(rate: 200), staff, cancellationToken: Ct).ResponseAsync).AdmissionRate);
    }

    [Fact]
    public async Task Release_less_than_a_microsecond_after_launch_is_a_validation_failure()
    {
        await using var t = await StartAsync();
        var request = NewLaunch();
        request.LaunchTime = new Timestamp { Seconds = request.LaunchTime.Seconds }; // PostgreSQL keeps whole microseconds: 500 ns later is the same instant there
        request.ReleaseDate = new Timestamp { Seconds = request.LaunchTime.Seconds, Nanos = 500 };

        await FieldFailsAsync(t.Admin.CreateLaunchAsync(request, Api.Staff(), cancellationToken: Ct), "release_date");

        await using var wider = await StartAsync(o => o.Config["Launch:AdmissionCeiling"] = "300");
        Assert.Equal(201, (await wider.Admin.CreateLaunchAsync(NewLaunch(rate: 201), Api.Staff(), cancellationToken: Ct).ResponseAsync).AdmissionRate);
    }

    [Fact]
    public async Task AdminGetLaunch_returns_every_stage_with_counts()
    {
        await using var t = await StartAsync();
        var draft = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var open = await Seed.LaunchAsync(t, products: [new SeedProduct(3, 10, 20), new SeedProduct(1, 5)]);
        await t.SqlAsync($"UPDATE launch_skus SET held = 2, sold = 3 WHERE sku_id = {open.Skus[0]}");
        await t.SqlAsync($"UPDATE launches SET version = 7, waiting_room_closed_at = now() WHERE id = {open.Id}");

        var (_, d) = await SendAsync(t, Request(HttpMethod.Get, $"/api/admin/launches/{draft.Id}"));
        Assert.Equal("draft", d.GetProperty("stage").GetString());
        Assert.Equal(1, d.GetProperty("products").GetArrayLength());
        Assert.Equal("0", d.GetProperty("version").GetString());

        var (_, o) = await SendAsync(t, Request(HttpMethod.Get, $"/api/admin/launches/{open.Id}", "admin"));
        Assert.Equal("preorder_open", o.GetProperty("stage").GetString());
        Assert.Equal("7", o.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.String, o.GetProperty("waitingRoomClosedAt").ValueKind);
        var products = o.GetProperty("products").EnumerateArray().ToDictionary(p => p.GetProperty("productId").GetString()!, p => p.GetProperty("purchaseLimit").GetInt32());
        Assert.Equal(new Dictionary<string, int> { [open.Products[0].ToString()] = 3, [open.Products[1].ToString()] = 1 }, products);
        var skus = o.GetProperty("skus").EnumerateArray().ToList();
        Assert.Equal(open.Skus.Select(s => s.ToString()).Order(StringComparer.Ordinal), skus.Select(s => s.GetProperty("skuId").GetString()!)); // by sku_id
        var first = skus.Single(s => s.GetProperty("skuId").GetString() == open.Skus[0].ToString());
        Assert.Equal((open.Products[0].ToString(), 10, 2, 3), (first.GetProperty("productId").GetString(), first.GetProperty("allocation").GetInt32(), first.GetProperty("held").GetInt32(), first.GetProperty("sold").GetInt32()));

        await Rpc.FailsAsync(t.Admin.AdminGetLaunchAsync(new() { LaunchId = Guid.NewGuid().ToString() }, Api.Staff(), cancellationToken: Ct), LaunchErrors.NotFoundReason);
        await FieldFailsAsync(t.Admin.AdminGetLaunchAsync(new() { LaunchId = "x" }, Api.Staff(), cancellationToken: Ct), "launch_id"); // R8
    }

    [Fact]
    public async Task AdminListLaunches_pages_newest_first()
    {
        await using var t = await StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await Seed.LaunchAsync(t, i % 2 == 0 ? LaunchStages.Draft : LaunchStages.Announced)).Id.ToString());
            t.Clock.Advance(TimeSpan.FromSeconds(1)); // distinct created_at
        }
        ids.Reverse();

        var seen = new List<string>();
        var sizes = new List<int>();
        var token = "";
        do
        {
            var query = "?pageSize=2" + (token == "" ? "" : $"&pageToken={token}");
            var (_, json) = await SendAsync(t, Request(HttpMethod.Get, "/api/admin/launches" + query)); // R19
            var page = json.GetProperty("launches").EnumerateArray().ToList();
            sizes.Add(page.Count);
            seen.AddRange(page.Select(l => l.GetProperty("id").GetString()!));
            Assert.All(page, l =>
            {
                Assert.True(!l.TryGetProperty("products", out var p) || p.GetArrayLength() == 0);
                Assert.True(!l.TryGetProperty("skus", out var s) || s.GetArrayLength() == 0);
                Assert.True(l.TryGetProperty("version", out _));
            });
            token = json.TryGetProperty("nextPageToken", out var next) ? next.GetString()! : "";
        } while (token != "" && sizes.Count < 10);

        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal(ids, seen);
    }

    [Fact]
    public async Task List_tie_break()
    {
        await using var t = await StartAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add((await Seed.LaunchAsync(t, LaunchStages.Draft)).Id.ToString()); // the clock stands still: one created_at for all
        var expected = ids.Order(StringComparer.Ordinal).Reverse().ToList(); // uuid order is byte order; the lower-case hex strings sort the same way

        var seen = new List<string>();
        var sizes = new List<int>();
        var token = "";
        do
        {
            var page = await t.Admin.AdminListLaunchesAsync(new() { PageSize = 2, PageToken = token }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
            sizes.Add(page.Launches.Count);
            seen.AddRange(page.Launches.Select(l => l.Id));
            token = page.NextPageToken;
        } while (token != "" && sizes.Count < 10);

        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task AdminListLaunches_rejects_bad_paging()
    {
        await using var t = await StartAsync();
        var staff = Api.Staff();

        await FieldFailsAsync(t.Admin.AdminListLaunchesAsync(new() { PageSize = 101 }, staff, cancellationToken: Ct), "page_size");
        await FieldFailsAsync(t.Admin.AdminListLaunchesAsync(new() { PageSize = -1 }, staff, cancellationToken: Ct), "page_size");
        await FieldFailsAsync(t.Admin.AdminListLaunchesAsync(new() { PageToken = "zz" }, staff, cancellationToken: Ct), "page_token");
        Assert.Empty((await t.Admin.AdminListLaunchesAsync(new() { PageSize = 100 }, staff, cancellationToken: Ct).ResponseAsync).Launches);
    }

    [Fact]
    public async Task Add_product_in_draft_adds_and_bumps_version()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var product = Guid.NewGuid();

        var view = await t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = product.ToString(), PurchaseLimit = 3 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.Equal(1, view.Version);
        Assert.Equal(2, view.Products.Count);
        Assert.Contains(view.Products, p => p.ProductId == product.ToString() && p.PurchaseLimit == 3);
        Assert.Equal(1, await VersionAsync(t, launch.Id));
        var audit = await Outbox.AuditAsync(t, "launch.product-added");
        Assert.Equal(("launch", launch.Id.ToString()), (audit.TargetType, audit.TargetId));
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged));
    }

    [Fact]
    public async Task Audit_details_for_adds()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        var (product, sku, newProduct) = (launch.Products[0], Guid.NewGuid(), Guid.NewGuid());
        t.Catalog.Sku(sku, product);

        await t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = newProduct.ToString(), PurchaseLimit = 3 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;
        await t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = product.ToString(), Allocation = 25 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        var productAdded = (await Outbox.AuditAsync(t, "launch.product-added")).Details;
        Assert.Equal((newProduct.ToString(), 3), (productAdded.GetProperty("productId").GetString(), productAdded.GetProperty("purchaseLimit").GetInt32()));
        var skuAdded = (await Outbox.AuditAsync(t, "launch.sku-added")).Details;
        Assert.Equal((sku.ToString(), product.ToString(), 25), (skuAdded.GetProperty("skuId").GetString(), skuAdded.GetProperty("productId").GetString(), skuAdded.GetProperty("allocation").GetInt32()));
    }

    [Fact]
    public async Task AddLaunchSku_stage_recheck()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, launch.Products[0]);
        // The launch leaves the editable stages while catalog is being asked: the read check passed, the check under the row lock must not.
        t.Catalog.Before = () => t.SqlAsync($"UPDATE launches SET stage = {LaunchStages.PreorderOpen} WHERE id = {launch.Id}");

        await Rpc.FailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = launch.Products[0].ToString(), Allocation = 5 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);

        Assert.Equal(1, t.Catalog.Count);
        await using var db = t.NewDb();
        Assert.Empty(await db.LaunchSkus.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(0, await VersionAsync(t, launch.Id));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded));
    }

    [Fact]
    public async Task A_product_can_be_in_one_launch_only()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var other = await Seed.LaunchAsync(t, LaunchStages.Draft);

        foreach (var product in new[] { launch.Products[0], other.Products[0] })
            await FieldFailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = product.ToString(), PurchaseLimit = 1 }, Api.Staff(), cancellationToken: Ct), "product_id");

        Assert.Equal(0, await VersionAsync(t, launch.Id));
    }

    [Fact]
    public async Task Add_validation()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var staff = Api.Staff();
        var l = launch.Id.ToString();

        await FieldFailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = "x", ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 1 }, staff, cancellationToken: Ct), "launch_id");
        await FieldFailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = l, ProductId = "x", PurchaseLimit = 1 }, staff, cancellationToken: Ct), "product_id");
        await FieldFailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = l, ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 0 }, staff, cancellationToken: Ct), "purchase_limit");
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = l, SkuId = "x", ProductId = launch.Products[0].ToString(), Allocation = 1 }, staff, cancellationToken: Ct), "sku_id");
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = l, SkuId = Guid.NewGuid().ToString(), ProductId = "x", Allocation = 1 }, staff, cancellationToken: Ct), "product_id");
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = l, SkuId = Guid.NewGuid().ToString(), ProductId = launch.Products[0].ToString(), Allocation = -1 }, staff, cancellationToken: Ct), "allocation");
        await Rpc.FailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = Guid.NewGuid().ToString(), ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 1 }, staff, cancellationToken: Ct), LaunchErrors.NotFoundReason);
        Assert.Equal(0, t.Catalog.Count);
    }

    [Fact]
    public async Task Add_sku_checks_catalog()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        var product = launch.Products[0];
        var staff = Api.Staff();
        using var metrics = new MetricCapture(t.Services);

        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, product);
        var view = await t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = product.ToString(), Allocation = 25 }, staff, cancellationToken: Ct).ResponseAsync;

        Assert.Equal(1, view.Version);
        var added = Assert.Single(view.Skus);
        Assert.Equal((sku.ToString(), product.ToString(), 25, 0, 0), (added.SkuId, added.ProductId, added.Allocation, added.Held, added.Sold));
        var audit = await Outbox.AuditAsync(t, "launch.sku-added");
        Assert.Equal(launch.Id.ToString(), audit.TargetId);
        Assert.Equal(25, metrics.Latest("allocation_available", ("launch", launch.Id.ToString()), ("sku", sku.ToString())));
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged)); // draft

        var calls = t.Catalog.Count;
        var unknown = Guid.NewGuid(); // catalog NOT_FOUND
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = unknown.ToString(), ProductId = product.ToString(), Allocation = 1 }, staff, cancellationToken: Ct), "sku_id");
        var otherProduct = Guid.NewGuid();
        t.Catalog.Sku(otherProduct, Guid.NewGuid()); // belongs to another product
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = otherProduct.ToString(), ProductId = product.ToString(), Allocation = 1 }, staff, cancellationToken: Ct), "sku_id");
        var taken = Guid.NewGuid();
        t.Catalog.Sku(taken, product, launchId: Guid.NewGuid()); // catalog says it is in a launch
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = taken.ToString(), ProductId = product.ToString(), Allocation = 1 }, staff, cancellationToken: Ct), "sku_id");
        Assert.Equal(calls + 3, t.Catalog.Count);

        // a product that is not in this launch: no catalog call
        var stranger = Guid.NewGuid();
        t.Catalog.Sku(stranger, Guid.NewGuid());
        calls = t.Catalog.Count;
        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = stranger.ToString(), ProductId = Guid.NewGuid().ToString(), Allocation = 1 }, staff, cancellationToken: Ct), "sku_id");
        Assert.Equal(calls, t.Catalog.Count);
        Assert.Equal(1, await VersionAsync(t, launch.Id));
    }

    [Fact]
    public async Task Catalog_unavailable_is_UNAVAILABLE()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, launch.Products[0]);
        var request = new AddLaunchSkuRequest { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = launch.Products[0].ToString(), Allocation = 5 };

        t.Catalog.Fail(StatusCode.Unavailable);
        var failed = await Assert.ThrowsAsync<RpcException>(() => t.Admin.AddLaunchSkuAsync(request, Api.Staff(), cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, failed.StatusCode);

        t.Catalog.Before = () => Task.Delay(TimeSpan.FromSeconds(4), Ct);
        var clock = Stopwatch.StartNew();
        var slow = await Assert.ThrowsAsync<RpcException>(() => t.Admin.AddLaunchSkuAsync(request, Api.Staff(), cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, slow.StatusCode);
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.5, 3.95); // the 3 s deadline, not the 4 s delay; wide for a loaded CI box
        t.Catalog.Before = null;

        await using var db = t.NewDb();
        Assert.Empty(await db.LaunchSkus.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(0, await VersionAsync(t, launch.Id));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded));
    }

    [Fact]
    public async Task A_sku_can_be_in_one_launch_only()
    {
        await using var t = await StartAsync();
        var first = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var second = await Seed.LaunchAsync(t, LaunchStages.Draft, products: new SeedProduct(2));
        t.Catalog.Sku(first.Skus[0], second.Products[0]); // catalog knows no launch for it, but the first launch has it

        await FieldFailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = second.Id.ToString(), SkuId = first.Skus[0].ToString(), ProductId = second.Products[0].ToString(), Allocation = 5 }, Api.Staff(), cancellationToken: Ct), "sku_id");

        Assert.Equal(0, await VersionAsync(t, second.Id));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffActionRecorded));
    }

    [Fact]
    public async Task L19_add_sku_while_announced_publishes_stage_changed()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, products: [new SeedProduct(2, 10), new SeedProduct(4)]);
        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, launch.Products[1]);

        await t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = launch.Products[1].ToString(), Allocation = 5 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        var events = await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged);
        var e = Assert.Single(events);
        Assert.Equal((launch.Id, "announced"), (e.LaunchId, e.Stage));
        Assert.Equal(new[] { (launch.Products[0], 2), (launch.Products[1], 4) }.OrderBy(p => p.Item1), e.Products.Select(p => (p.ProductId, p.PurchaseLimit)).OrderBy(p => p.ProductId));
        Assert.Equal(new[] { (launch.Skus[0], launch.Products[0]), (sku, launch.Products[1]) }.OrderBy(s => s.Item1), e.Skus.Select(s => (s.SkuId, s.ProductId)).OrderBy(s => s.SkuId));

        var product = Guid.NewGuid();
        await t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = product.ToString(), PurchaseLimit = 6 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        events = await Outbox.PayloadsAsync<LaunchStageChangedV1>(t, LaunchMessages.StageChanged);
        Assert.Equal(2, events.Count);
        Assert.Contains(events[1].Products, p => p.ProductId == product && p.PurchaseLimit == 6);
        Assert.Equal(2, await VersionAsync(t, launch.Id));
    }

    [Theory]
    [InlineData(LaunchStages.PreorderOpen)]
    [InlineData(LaunchStages.Released)]
    public async Task L20_add_sku_or_product_while_preorder_open_is_INVALID_STAGE(string stage)
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, stage);
        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, launch.Products[0]);

        await Rpc.FailsAsync(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = launch.Products[0].ToString(), Allocation = 5 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);
        await Rpc.FailsAsync(t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 1 }, Api.Staff(), cancellationToken: Ct), LaunchErrors.InvalidStage);

        Assert.Equal(0, t.Catalog.Count);
        Assert.Equal(0, await VersionAsync(t, launch.Id));
        Assert.Empty(await Outbox.EnvelopesAsync(t, LaunchMessages.StageChanged));
    }

    [Fact]
    public async Task Writes_drop_the_state_cache()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Announced, products: new SeedProduct(2));
        var sku = Guid.NewGuid();
        t.Catalog.Sku(sku, launch.Products[0]);
        await t.Customer.GetLaunchAsync(new() { LaunchId = launch.Id.ToString() }, cancellationToken: Ct).ResponseAsync; // fills wr:{L}:state
        Assert.True(await t.Redis.KeyExistsAsync(RedisKeys.State(launch.Id)));

        await t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = sku.ToString(), ProductId = launch.Products[0].ToString(), Allocation = 5 }, Api.Staff(), cancellationToken: Ct).ResponseAsync;

        Assert.False(await t.Redis.KeyExistsAsync(RedisKeys.State(launch.Id)));
    }

    [Theory]
    [InlineData("support")]
    [InlineData("warehouse")]
    [InlineData("customer")]
    public async Task Other_roles_are_ROLE_NOT_ALLOWED(string role)
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, LaunchStages.Draft);
        var caller = role == "customer" ? Api.Customer(Guid.NewGuid()) : Api.Staff(role);

        async Task Denied<T>(AsyncUnaryCall<T> call)
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Rpc.Info(e).Reason);
        }

        await Denied(t.Admin.CreateLaunchAsync(NewLaunch(), caller, cancellationToken: Ct));
        await Denied(t.Admin.AdminGetLaunchAsync(new() { LaunchId = launch.Id.ToString() }, caller, cancellationToken: Ct));
        await Denied(t.Admin.AdminListLaunchesAsync(new(), caller, cancellationToken: Ct));
        await Denied(t.Admin.AddLaunchProductAsync(new() { LaunchId = launch.Id.ToString(), ProductId = Guid.NewGuid().ToString(), PurchaseLimit = 1 }, caller, cancellationToken: Ct));
        await Denied(t.Admin.AddLaunchSkuAsync(new() { LaunchId = launch.Id.ToString(), SkuId = Guid.NewGuid().ToString(), ProductId = launch.Products[0].ToString(), Allocation = 1 }, caller, cancellationToken: Ct));
        Assert.Equal(0, t.Catalog.Count);
    }
}
