using System.Buffers.Text;
using System.Text;
using Bloom.Catalog.Contracts;
using Bloom.Launch.Context;
using Bloom.Launch.Data;
using Bloom.Launch.Domain;
using Bloom.Launch.Errors;
using Bloom.Launch.Observability;
using Bloom.Launch.WaitingRoom;
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
    LaunchDbContext db, OutboxWriter outbox, Audit audit, StateCache cache, LaunchMetrics metrics, LaunchOptions options,
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

    // The six edit and publish methods are built in the next task.
    public override Task<AdminLaunch> SetAllocation(SetAllocationRequest request, ServerCallContext context) => throw new NotImplementedException();
    public override Task<AdminLaunch> SetPurchaseLimit(SetPurchaseLimitRequest request, ServerCallContext context) => throw new NotImplementedException();
    public override Task<AdminLaunch> SetReleaseDate(SetReleaseDateRequest request, ServerCallContext context) => throw new NotImplementedException();
    public override Task<AdminLaunch> SetLaunchTime(SetLaunchTimeRequest request, ServerCallContext context) => throw new NotImplementedException();
    public override Task<AdminLaunch> SetAdmissionRate(SetAdmissionRateRequest request, ServerCallContext context) => throw new NotImplementedException();
    public override Task<AdminLaunch> PublishLaunch(PublishLaunchRequest request, ServerCallContext context) => throw new NotImplementedException();

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

    private static void RequireEditable(LaunchRow row)
    {
        if (!Editable.Contains(row.Stage)) throw LaunchErrors.Error(LaunchErrors.InvalidStage, $"A launch in stage {row.Stage} can't take this change.");
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
