using System.Buffers.Text;
using System.Text;
using Bloom.Catalog.Contracts;
using Bloom.Launch.Context;
using Bloom.Launch.Data;
using Bloom.Launch.Domain;
using Bloom.Launch.Errors;
using Bloom.Launch.Observability;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Launch.Services;

/// <summary>
/// 01-launch §5.1 admin API for staff (<c>catalog_manager</c>, <c>admin</c>). Writes follow plan R9: one transaction, the launch row locked first,
/// <c>version + 1</c>, the audit row and, from <c>announced</c> on, the stage-changed event (R10); after commit the state cache is dropped.
/// </summary>
public sealed class AdminService(
    LaunchDbContext db, OutboxWriter outbox, Audit audit, ConcurrentUpdates concurrent, StateCache cache, LaunchMetrics metrics, LaunchOptions options,
    CatalogInternal.CatalogInternalClient catalog, ILogger<AdminService> logger) : LaunchAdmin.LaunchAdminBase
{
    private const int DefaultPageSize = 20, MaxPageSize = 100;
    private static readonly TimeSpan CatalogDeadline = TimeSpan.FromSeconds(3); // under the gateway's 5 s route timeout (spec §7)
    private static readonly string[] Editable = [LaunchStages.Draft, LaunchStages.Announced];

    public override async Task<AdminLaunch> CreateLaunch(CreateLaunchRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var name = request.Name.Trim();
        if (name == "") throw LaunchErrors.Validation("name", "name must not be empty.");
        var launchTime = Time(request.LaunchTime, "launch_time");
        var releaseDate = Time(request.ReleaseDate, "release_date");
        if (releaseDate <= launchTime) throw LaunchErrors.Validation("release_date", "release_date must be after launch_time.");
        if (request.AdmissionRate < 1 || request.AdmissionRate > options.AdmissionCeiling)
            throw LaunchErrors.Validation("admission_rate", $"admission_rate must be from 1 to {options.AdmissionCeiling}.");

        var now = await db.NowAsync(context.CancellationToken);
        var row = new LaunchRow
        {
            Id = Guid.CreateVersion7(), Name = name, Stage = LaunchStages.Draft, LaunchTime = launchTime, ReleaseDate = releaseDate,
            AdmissionRate = request.AdmissionRate, CreatedAt = now, UpdatedAt = now,
        };
        db.Launches.Add(row);
        audit.Record(caller, "launch.created", row.Id);
        await db.SaveChangesAsync(context.CancellationToken);
        return Views.Admin(row);
    }

    public override async Task<AdminLaunch> AdminGetLaunch(AdminGetLaunchRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Staff);
        var id = LaunchId(request.LaunchId);
        return await Views.AdminAsync(db, id, context.CancellationToken) ?? throw LaunchErrors.NotFound("The launch");
    }

    public override async Task<AdminListLaunchesResponse> AdminListLaunches(AdminListLaunchesRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Staff);
        if (request.PageSize is < 0 or > MaxPageSize) throw LaunchErrors.Validation("page_size", $"Use a page size from 1 to {MaxPageSize}.");
        var size = request.PageSize == 0 ? DefaultPageSize : request.PageSize;

        var rows = db.Launches.AsNoTracking();
        if (request.PageToken != "")
        {
            var (at, id) = ReadPageToken(request.PageToken);
            rows = rows.Where(l => l.CreatedAt < at || (l.CreatedAt == at && l.Id.CompareTo(id) < 0));
        }
        var page = await rows.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Take(size + 1).ToListAsync(context.CancellationToken);
        var response = new AdminListLaunchesResponse { Launches = { page.Take(size).Select(Views.Admin) } };
        if (page.Count > size) response.NextPageToken = WritePageToken(page[size - 1]);
        return response;
    }

    public override async Task<AdminLaunch> AddLaunchProduct(AddLaunchProductRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var ct = context.CancellationToken;
        var launchId = LaunchId(request.LaunchId);
        var productId = Uuid(request.ProductId, "product_id");
        if (request.PurchaseLimit <= 0) throw LaunchErrors.Validation("purchase_limit", "purchase_limit must be greater than 0.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(launchId, ct);
        RequireEditable(row);
        db.LaunchProducts.Add(new() { LaunchId = launchId, ProductId = productId, PurchaseLimit = request.PurchaseLimit });
        audit.Record(caller, "launch.product-added", launchId, new { productId, purchaseLimit = request.PurchaseLimit });
        await BumpAndSaveAsync(row, "product_id", "This product is already in a launch.", "ux_launch_products_product_id", "PK_launch_products", ct);
        await tx.CommitAsync(ct);

        await cache.DropAsync(launchId);
        return await Views.AdminAsync(db, launchId, ct) ?? throw LaunchErrors.NotFound("The launch");
    }

    public override async Task<AdminLaunch> AddLaunchSku(AddLaunchSkuRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var ct = context.CancellationToken;
        var launchId = LaunchId(request.LaunchId);
        var skuId = Uuid(request.SkuId, "sku_id");
        var productId = Uuid(request.ProductId, "product_id");
        if (request.Allocation < 0) throw LaunchErrors.Validation("allocation", "allocation must be 0 or more.");

        // Read check first: no catalog call for a launch or product that can't take the SKU (spec §7).
        var launch = await db.Launches.AsNoTracking().SingleOrDefaultAsync(l => l.Id == launchId, ct) ?? throw LaunchErrors.NotFound("The launch");
        RequireEditable(launch);
        await RequireProductAsync(launchId, productId, ct);
        await CheckCatalogAsync(skuId, productId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(launchId, ct);
        RequireEditable(row);
        await RequireProductAsync(launchId, productId, ct);
        db.LaunchSkus.Add(new() { LaunchId = launchId, SkuId = skuId, ProductId = productId, Allocation = request.Allocation });
        audit.Record(caller, "launch.sku-added", launchId, new { skuId, productId, allocation = request.Allocation });
        await BumpAndSaveAsync(row, "sku_id", "This SKU is already in a launch.", "ux_launch_skus_sku_id", "PK_launch_skus", ct);
        await tx.CommitAsync(ct);

        await cache.DropAsync(launchId);
        metrics.SetAvailable(launchId, skuId, request.Allocation); // held = sold = 0 on a new SKU
        return await Views.AdminAsync(db, launchId, ct) ?? throw LaunchErrors.NotFound("The launch");
    }

    public override async Task<AdminLaunch> SetAllocation(SetAllocationRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var ct = context.CancellationToken;
        var launchId = LaunchId(request.LaunchId);
        var skuId = Uuid(request.SkuId, "sku_id");
        var allocation = request.Allocation;
        if (allocation < 0) throw LaunchErrors.Validation("allocation", "allocation must be 0 or more.");

        long available = 0;
        var view = await EditAsync(launchId, request.Version, publishes: false, async row =>
        {
            var sku = await db.LaunchSkus.AsNoTracking().SingleOrDefaultAsync(s => s.LaunchId == launchId && s.SkuId == skuId, ct) ?? throw LaunchErrors.NotFound("The SKU");
            if (row.Stage == LaunchStages.Released) throw StageError(row);
            if (row.Stage != LaunchStages.Draft && allocation < sku.Allocation)
                throw LaunchErrors.Error(LaunchErrors.AllocationDecreaseNotAllowed, "Allocation can only increase once a launch is announced.");
            // R11: the guard computes in bigint; LR-01 holds whatever a concurrent hold does.
            var returned = await db.Database.SqlQuery<long>(
                $"UPDATE launch_skus SET allocation = {allocation} WHERE launch_id = {launchId} AND sku_id = {skuId} AND held::bigint + sold <= {allocation} RETURNING allocation - held - sold AS \"Value\"")
                .ToListAsync(ct);
            if (returned.Count != 1) throw LaunchErrors.Error(LaunchErrors.AllocationDecreaseNotAllowed, "Allocation can't go below what is held or sold.");
            available = returned[0];
            audit.Record(caller, "launch.allocation-changed", launchId, new { skuId, before = sku.Allocation, after = allocation });
        }, ct);
        metrics.SetAvailable(launchId, skuId, available);
        return view;
    }

    public override async Task<AdminLaunch> SetPurchaseLimit(SetPurchaseLimitRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var ct = context.CancellationToken;
        var launchId = LaunchId(request.LaunchId);
        var productId = Uuid(request.ProductId, "product_id");
        if (request.PurchaseLimit <= 0) throw LaunchErrors.Validation("purchase_limit", "purchase_limit must be greater than 0.");

        return await EditAsync(launchId, request.Version, publishes: true, async row =>
        {
            var product = await db.LaunchProducts.SingleOrDefaultAsync(p => p.LaunchId == launchId && p.ProductId == productId, ct) ?? throw LaunchErrors.NotFound("The product");
            if (!Editable.Contains(row.Stage)) throw LaunchErrors.Error(LaunchErrors.PurchaseLimitLocked, $"The purchase limit is locked in stage {row.Stage}.");
            audit.Record(caller, "launch.purchase-limit-changed", launchId, new { productId, before = product.PurchaseLimit, after = request.PurchaseLimit });
            product.PurchaseLimit = request.PurchaseLimit;
        }, ct);
    }

    public override async Task<AdminLaunch> SetReleaseDate(SetReleaseDateRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var launchId = LaunchId(request.LaunchId);
        var date = Time(request.ReleaseDate, "release_date");

        return await EditAsync(launchId, request.Version, publishes: true, async (row, now) =>
        {
            RequireBeforeReleased(row);
            if (date <= now || date <= row.LaunchTime) throw LaunchErrors.Validation("release_date", "release_date must be in the future and after launch_time.");
            audit.Record(caller, "launch.release-date-changed", launchId, new { before = row.ReleaseDate, after = date });
            row.ReleaseDate = date;
        }, context.CancellationToken);
    }

    public override async Task<AdminLaunch> SetLaunchTime(SetLaunchTimeRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var launchId = LaunchId(request.LaunchId);
        var time = Time(request.LaunchTime, "launch_time");

        return await EditAsync(launchId, request.Version, publishes: false, async row =>
        {
            if (row.Stage != LaunchStages.Draft) throw StageError(row);
            if (time >= row.ReleaseDate) throw LaunchErrors.Validation("launch_time", "launch_time must be before release_date.");
            audit.Record(caller, "launch.launch-time-changed", launchId, new { before = row.LaunchTime, after = time });
            row.LaunchTime = time;
        }, context.CancellationToken);
    }

    public override async Task<AdminLaunch> SetAdmissionRate(SetAdmissionRateRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var launchId = LaunchId(request.LaunchId);
        if (request.AdmissionRate < 1 || request.AdmissionRate > options.AdmissionCeiling)
            throw LaunchErrors.Validation("admission_rate", $"admission_rate must be from 1 to {options.AdmissionCeiling}.");

        return await EditAsync(launchId, request.Version, publishes: false, async row =>
        {
            RequireBeforeReleased(row);
            audit.Record(caller, "launch.admission-rate-changed", launchId, new { before = row.AdmissionRate, after = request.AdmissionRate });
            row.AdmissionRate = request.AdmissionRate;
        }, context.CancellationToken);
    }

    public override async Task<AdminLaunch> PublishLaunch(PublishLaunchRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Staff);
        var ct = context.CancellationToken;
        var launchId = LaunchId(request.LaunchId);

        return await EditAsync(launchId, request.Version, publishes: true, async (row, now) =>
        {
            if (row.Stage != LaunchStages.Draft) throw StageError(row);
            // Owner item 3: at least one SKU, and every product has one.
            var products = await db.LaunchProducts.AsNoTracking().Where(p => p.LaunchId == launchId).Select(p => p.ProductId).ToListAsync(ct);
            var withSkus = await db.LaunchSkus.AsNoTracking().Where(s => s.LaunchId == launchId).Select(s => s.ProductId).Distinct().ToListAsync(ct);
            if (withSkus.Count == 0) throw LaunchErrors.Validation("skus", "A launch needs at least one SKU.");
            if (products.FirstOrDefault(p => !withSkus.Contains(p)) is var bare && bare != default)
                throw LaunchErrors.Validation("skus", $"Product {bare:D} has no SKU.");
            if (row.LaunchTime <= now) throw LaunchErrors.Validation("launch_time", "launch_time must be in the future.");
            row.Stage = LaunchStages.Announced;
            audit.Record(caller, "launch.published", launchId);
        }, ct);
    }

    /// <summary>Catalog must know the SKU, under this product, in no launch. Anything else catalog says, or a timeout, is <c>UNAVAILABLE</c> (§5.2).</summary>
    private async Task CheckCatalogAsync(Guid skuId, Guid productId, CancellationToken ct)
    {
        GetSkuResponse sku;
        try
        {
            sku = await catalog.GetSkuAsync(new GetSkuRequest { SkuId = skuId.ToString("D") }, deadline: DateTime.UtcNow + CatalogDeadline, cancellationToken: ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            throw LaunchErrors.Validation("sku_id", "Catalog does not know this SKU.");
        }
        catch (RpcException e)
        {
            logger.LogWarning(e, "Catalog GetSku failed with {Status}.", e.StatusCode);
            throw LaunchErrors.Unavailable("Catalog is unavailable.");
        }
        if (sku.ProductId != productId.ToString("D")) throw LaunchErrors.Validation("sku_id", "This SKU belongs to another product.");
        if (sku.LaunchId != "") throw LaunchErrors.Validation("sku_id", "This SKU is already in a launch.");
    }

    /// <summary>Plan R9 start: the launch row locked for the transaction. Tracked, so the caller bumps its version.</summary>
    private async Task<LaunchRow> LockAsync(Guid id, CancellationToken ct) =>
        (await db.Launches.FromSql($"SELECT * FROM launches WHERE id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
        ?? throw LaunchErrors.NotFound("The launch");

    /// <summary>Plan R9 end: <c>version + 1</c> and <c>updated_at = now()</c>, then the changes; from <c>announced</c> on, the stage-changed event (R10).
    /// A unique violation of <paramref name="index"/> or <paramref name="primaryKey"/> is <c>VALIDATION_FAILED</c> on <paramref name="field"/>.</summary>
    private async Task BumpAndSaveAsync(LaunchRow row, string field, string message, string index, string primaryKey, CancellationToken ct)
    {
        row.Version++;
        row.UpdatedAt = await db.NowAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var constraint } && (constraint == index || constraint == primaryKey))
        {
            throw LaunchErrors.Validation(field, message);
        }
        if (row.Stage == LaunchStages.Announced)
        {
            await StageEvents.WriteAsync(db, outbox, row.Id, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Plan R9 for a write that takes a <c>version</c>, in one transaction: the launch row locked, then the version check
    /// (<see cref="ConcurrentUpdates.UpdateVersionedAsync"/> adds 1 and sets <c>updated_at</c> to the database's <c>now()</c>), then <paramref name="change"/>
    /// (stage rule first, then the change; a throw rolls the bump back). <paramref name="publishes"/>: a launch that is no longer a draft gets the stage-changed event (R10).
    /// After commit the state cache is dropped.
    /// </summary>
    private async Task<AdminLaunch> EditAsync(Guid launchId, long version, bool publishes, Func<LaunchRow, DateTimeOffset, Task> change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(launchId, ct);
        var now = await db.NowAsync(ct);
        await concurrent.UpdateVersionedAsync("launch", db.Launches.Where(l => l.Id == launchId), version, s => s.SetProperty(l => l.UpdatedAt, now), LaunchErrors.NotFoundReason, ct);
        await change(row, now);
        await db.SaveChangesAsync(ct);
        if (publishes && row.Stage != LaunchStages.Draft)
        {
            await StageEvents.WriteAsync(db, outbox, launchId, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        await cache.DropAsync(launchId);
        return await Views.AdminAsync(db, launchId, ct) ?? throw LaunchErrors.NotFound("The launch");
    }

    private Task<AdminLaunch> EditAsync(Guid launchId, long version, bool publishes, Func<LaunchRow, Task> change, CancellationToken ct) =>
        EditAsync(launchId, version, publishes, (row, _) => change(row), ct);

    private static RpcException StageError(LaunchRow row) => LaunchErrors.Error(LaunchErrors.InvalidStage, $"A launch in stage {row.Stage} can't take this change.");

    private static void RequireBeforeReleased(LaunchRow row)
    {
        if (row.Stage == LaunchStages.Released) throw StageError(row);
    }

    private static void RequireEditable(LaunchRow row)
    {
        if (!Editable.Contains(row.Stage)) throw StageError(row);
    }

    private async Task RequireProductAsync(Guid launchId, Guid productId, CancellationToken ct)
    {
        if (!await db.LaunchProducts.AnyAsync(p => p.LaunchId == launchId && p.ProductId == productId, ct))
            throw LaunchErrors.Validation("sku_id", "The product is not in this launch.");
    }

    /// <summary>R8: on admin methods an id that is not a UUID is a validation failure.</summary>
    private static Guid LaunchId(string raw) => Uuid(raw, "launch_id");

    private static Guid Uuid(string raw, string field) =>
        Guid.TryParseExact(raw, "D", out var id) ? id : throw LaunchErrors.Validation(field, $"{field} must be a UUID.");

    private static DateTimeOffset Time(Timestamp? value, string field)
    {
        if (value is null) throw LaunchErrors.Validation(field, $"{field} is required.");
        try
        {
            return value.ToDateTimeOffset();
        }
        catch (ArgumentOutOfRangeException)
        {
            throw LaunchErrors.Validation(field, $"{field} is not a valid time.");
        }
    }

    private static string WritePageToken(LaunchRow last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{last.CreatedAt.UtcTicks}|{last.Id}"));

    private static (DateTimeOffset At, Guid Id) ReadPageToken(string token)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], out var ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks && Guid.TryParse(parts[1], out var id))
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { }
        throw LaunchErrors.Validation("page_token", "The page token is not valid.");
    }
}
