using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;
using OptionType = Bloom.Catalog.Data.OptionType;
using OptionValue = Bloom.Catalog.Data.OptionValue;
using Product = Bloom.Catalog.Data.Product;
using Sku = Bloom.Catalog.Data.Sku;

namespace Bloom.Catalog.Services;

public sealed partial class AdminService
{
    private const string SkuCodeIndex = "ux_skus_code";
    private const string SkuOptionKeyIndex = "ux_skus_product_option_key";

    private sealed record OptionValueInput(Guid? Id, string Value);

    private sealed record OptionTypeInput(Guid? Id, string Name, List<OptionValueInput> Values);

    /// <summary>What one SKU write changes: the column setters and the audit entry.</summary>
    private sealed record SkuChange(Action<UpdateSettersBuilder<Sku>> Set, string Action, object Details);

    public override async Task<AdminProduct> SetOptions(SetOptionsRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var wanted = OptionInputs(request.OptionTypes);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var id)) throw CatalogErrors.NotFoundError("Product");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = await LockProductAsync(id, ct);
        if (product.Kind == ProductKinds.WarrantyPlan) throw CatalogErrors.Validation("option_types", "A warranty plan has no options.");
        var types = await db.OptionTypes.Where(t => t.ProductId == id).OrderBy(t => t.Position).ThenBy(t => t.Id).ToListAsync(ct);
        var values = await db.OptionValues.Where(v => db.OptionTypes.Any(t => t.Id == v.OptionTypeId && t.ProductId == id))
            .OrderBy(v => v.Position).ThenBy(v => v.Id).ToListAsync(ct);

        foreach (var type in wanted) // an id must be one of this product's own
        {
            if (type.Id is { } typeId && types.All(t => t.Id != typeId)) throw CatalogErrors.NotFoundError("Option type");
            foreach (var value in type.Values)
                if (value.Id is { } valueId && !values.Any(v => v.Id == valueId && v.OptionTypeId == type.Id))
                    throw CatalogErrors.NotFoundError("Option value");
        }

        string Signature(IEnumerable<(string? Id, string Name, IEnumerable<(string? Id, string Value)> Values)> list) =>
            string.Join('|', list.Select(t => $"{t.Id}:{t.Name}:{string.Join(',', t.Values.Select(v => $"{v.Id}:{v.Value}"))}"));
        var current = Signature(types.Select(t => ((string?)t.Id.ToString(), t.Name,
            values.Where(v => v.OptionTypeId == t.Id).Select(v => ((string?)v.Id.ToString(), v.Value)))));
        var requested = Signature(wanted.Select(t => (t.Id?.ToString(), t.Name, t.Values.Select(v => (v.Id?.ToString(), v.Value)))));
        if (current == requested) return await LoadProductAsync(id, ct); // nothing to change: no write, no audit (R13)

        if (await db.Skus.AnyAsync(s => s.ProductId == id, ct))
        {
            var kept = wanted.SelectMany(t => t.Values).Select(v => v.Id).ToHashSet();
            var used = await db.SkuOptionValues.Where(l => db.Skus.Any(s => s.Id == l.SkuId && s.ProductId == id)).Select(l => l.OptionValueId).Distinct().ToListAsync(ct);
            if (wanted.Any(t => t.Id is null) || wanted.Count != types.Count || used.Any(u => !kept.Contains(u)))
                throw CatalogErrors.Validation("option_types", "While the product has SKUs, an option type can't be added or removed and a value a SKU uses can't be removed.");
        }

        await concurrent.UpdateVersionedAsync("product", db.Products.Where(p => p.Id == id), request.Version,
            s => s.SetProperty(p => p.UpdatedAt, clock.GetUtcNow()), CatalogErrors.NotFound, ct);

        var keptTypes = wanted.Select(t => t.Id).ToHashSet();
        var keptValues = wanted.SelectMany(t => t.Values).Select(v => v.Id).ToHashSet();
        db.OptionValues.RemoveRange(values.Where(v => !keptValues.Contains(v.Id)));
        db.OptionTypes.RemoveRange(types.Where(t => !keptTypes.Contains(t.Id)));
        for (var i = 0; i < wanted.Count; i++)
        {
            var type = types.SingleOrDefault(t => t.Id == wanted[i].Id);
            if (type is null) db.OptionTypes.Add(type = new OptionType { Id = Guid.CreateVersion7(), ProductId = id, Name = wanted[i].Name });
            type.Name = wanted[i].Name;
            type.Position = i;
            for (var j = 0; j < wanted[i].Values.Count; j++)
            {
                var value = values.SingleOrDefault(v => v.Id == wanted[i].Values[j].Id);
                if (value is null) db.OptionValues.Add(value = new OptionValue { Id = Guid.CreateVersion7(), OptionTypeId = type.Id, Value = wanted[i].Values[j].Value });
                value.Value = wanted[i].Values[j].Value;
                value.Position = j;
            }
        }
        audit.Record(caller, "catalog.options-set", "product", id);
        await db.SaveChangesAsync(ct);
        await derived.RefreshAsync(id);
        await InvalidateProductAsync(id, isPlan: false, ct);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(id, ct);
    }

    public override async Task<AdminSku> CreateSku(CreateSkuRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var code = Inputs.SkuCode(request.Code);
        var price = Inputs.PriceCents(request.PriceCents);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Product");
        var valueIds = request.OptionValueIds.Select(raw => Guid.TryParse(raw, out var v) ? v : throw CatalogErrors.NotFoundError("Option value")).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = await LockProductAsync(productId, ct);
        var types = await db.OptionTypes.AsNoTracking().Where(t => t.ProductId == productId).OrderBy(t => t.Position).ToListAsync(ct);
        var chosen = await db.OptionValues.AsNoTracking().Where(v => valueIds.Contains(v.Id)).ToListAsync(ct);
        if (chosen.Count != valueIds.Distinct().Count() || chosen.Any(v => types.All(t => t.Id != v.OptionTypeId)))
            throw CatalogErrors.NotFoundError("Option value");
        if (valueIds.Count != types.Count || chosen.Select(v => v.OptionTypeId).Distinct().Count() != types.Count)
            throw CatalogErrors.Validation("option_value_ids", "A SKU needs exactly one value of each option type.");

        var row = new Sku
        {
            Id = Guid.CreateVersion7(), ProductId = productId, Code = code, PriceCents = price,
            OptionKey = string.Join(',', valueIds.Select(v => v.ToString("D")).Order(StringComparer.Ordinal)),
        };
        db.Skus.Add(row);
        db.SkuOptionValues.AddRange(valueIds.Select(v => new SkuOptionValue { SkuId = row.Id, OptionValueId = v }));
        audit.Record(caller, "catalog.sku-created", "sku", row.Id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (Db.IsUniqueViolation(e, SkuCodeIndex)) { throw SkuCodeTaken(); }
        catch (DbUpdateException e) when (Db.IsUniqueViolation(e, SkuOptionKeyIndex)) { throw DuplicateCombination(); }
        await derived.RefreshAsync(productId);
        await InvalidateProductAsync(productId, product.Kind == ProductKinds.WarrantyPlan, ct);
        await tx.CommitAsync(ct);
        return ToProto(row, chosen.OrderBy(v => types.Single(t => t.Id == v.OptionTypeId).Position).Select(v => v.Id));
    }

    public override async Task<AdminSku> UpdateSku(UpdateSkuRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var code = Inputs.SkuCode(request.Code);
        return await ChangeSkuAsync(caller, request.Id, request.Version, context.CancellationToken, sku => sku.Code == code ? null
            : new SkuChange(s => s.SetProperty(x => x.Code, code), "catalog.sku-updated",
                new { before = new { code = sku.Code }, after = new { code } }));
    }

    public override async Task<AdminSku> SetSkuPrice(SetSkuPriceRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var price = Inputs.PriceCents(request.PriceCents);
        return await ChangeSkuAsync(caller, request.Id, request.Version, context.CancellationToken, sku => sku.PriceCents == price ? null
            : new SkuChange(s => s.SetProperty(x => x.PriceCents, price), "catalog.price-changed",
                new { before = new { priceCents = sku.PriceCents }, after = new { priceCents = price } }));
    }

    public override async Task<AdminSku> SetSkuActive(SetSkuActiveRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        return await ChangeSkuAsync(caller, request.Id, request.Version, context.CancellationToken, sku => sku.Active == request.Active ? null
            : new SkuChange(s => s.SetProperty(x => x.Active, request.Active), request.Active ? "catalog.sku-activated" : "catalog.sku-deactivated",
                new { }));
    }

    public override async Task<AdminProduct> SetPlanEligibility(SetPlanEligibilityRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.PlanProductId, out var planId)) throw CatalogErrors.NotFoundError("Product");
        var wanted = request.EligibleProductIds
            .Select(raw => Guid.TryParse(raw, out var id) ? id : throw CatalogErrors.NotFoundError("Product")).Distinct().ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plan = await LockProductAsync(planId, ct);
        if (plan.Kind != ProductKinds.WarrantyPlan) throw CatalogErrors.Validation("plan_product_id", "Only a warranty plan has eligible products.");
        var found = await db.Products.AsNoTracking().Where(p => wanted.Contains(p.Id)).Select(p => new { p.Id, p.Kind }).ToListAsync(ct);
        if (found.Count != wanted.Count) throw CatalogErrors.NotFoundError("Product");
        if (found.Any(p => p.Kind != ProductKinds.Physical)) throw CatalogErrors.Validation("eligible_product_ids", "Only physical products can be eligible.");

        var current = await db.PlanEligibilities.Where(e => e.PlanProductId == planId).Select(e => e.EligibleProductId).ToListAsync(ct);
        if (current.Count == wanted.Count && !current.Except(wanted).Any()) return await LoadProductAsync(planId, ct); // no write, no audit (R13)

        await concurrent.UpdateVersionedAsync("product", db.Products.Where(p => p.Id == planId), request.Version,
            s => s.SetProperty(p => p.UpdatedAt, clock.GetUtcNow()), CatalogErrors.NotFound, ct);
        var removed = current.Except(wanted).ToList();
        await db.PlanEligibilities.Where(e => e.PlanProductId == planId && removed.Contains(e.EligibleProductId)).ExecuteDeleteAsync(ct);
        db.PlanEligibilities.AddRange(wanted.Except(current).Select(d => new PlanEligibility { PlanProductId = planId, EligibleProductId = d }));
        string[] Ids(IEnumerable<Guid> ids) => ids.Select(i => i.ToString()).Order(StringComparer.Ordinal).ToArray();
        audit.Record(caller, "catalog.plan-eligibility-set", "product", planId,
            new { before = new { eligibleProductIds = Ids(current) }, after = new { eligibleProductIds = Ids(wanted) } });
        foreach (var device in current.Union(wanted)) invalidation.Product(device); // the plan shows on the page of each device it covered or covers
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(planId, ct);
    }

    /// <summary>
    /// One SKU write: locks the SKU's product (so the derived columns are computed after earlier SKU writes), applies the change
    /// <paramref name="change"/> picks for the current row (null = nothing to change: success without a write, R13), bumps the SKU version,
    /// refreshes the derived columns and drops the product's cache entries.
    /// </summary>
    private async Task<AdminSku> ChangeSkuAsync(Caller caller, string rawId, long version, CancellationToken ct, Func<Sku, SkuChange?> change)
    {
        if (!Guid.TryParse(rawId, out var id)) throw CatalogErrors.NotFoundError("SKU");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = (await db.Products.FromSql($"SELECT p.* FROM products p JOIN skus s ON s.product_id = p.id WHERE s.id = {id} FOR UPDATE OF p")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault() ?? throw CatalogErrors.NotFoundError("SKU");
        var sku = await db.Skus.AsNoTracking().SingleAsync(s => s.Id == id, ct);
        if (change(sku) is not { } c) return await LoadSkuAsync(sku, ct);

        try { await concurrent.UpdateVersionedAsync("sku", db.Skus.Where(s => s.Id == id), version, c.Set, CatalogErrors.NotFound, ct); }
        catch (PostgresException e) when (Db.IsUniqueViolation(e, SkuCodeIndex)) { throw SkuCodeTaken(); }
        audit.Record(caller, c.Action, "sku", id, c.Details);
        await db.SaveChangesAsync(ct);
        await derived.RefreshAsync(product.Id);
        await InvalidateProductAsync(product.Id, product.Kind == ProductKinds.WarrantyPlan, ct);
        await tx.CommitAsync(ct);
        return await LoadSkuAsync(await db.Skus.AsNoTracking().SingleAsync(s => s.Id == id, ct), ct);
    }

    /// <summary>Product page and listings; for a plan also the page of every device it covers (R10).</summary>
    private async Task InvalidateProductAsync(Guid id, bool isPlan, CancellationToken ct)
    {
        invalidation.Product(id);
        invalidation.Listings();
        if (isPlan)
            foreach (var device in await db.PlanEligibilities.Where(e => e.PlanProductId == id).Select(e => e.EligibleProductId).ToListAsync(ct))
                invalidation.Product(device);
    }

    private async Task<Product> LockProductAsync(Guid id, CancellationToken ct) =>
        (await db.Products.FromSql($"SELECT * FROM products WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        ?? throw CatalogErrors.NotFoundError("Product");

    private async Task<AdminSku> LoadSkuAsync(Sku sku, CancellationToken ct) =>
        ToProto(sku, await db.SkuOptionValues.AsNoTracking().Where(l => l.SkuId == sku.Id)
            .Join(db.OptionValues, l => l.OptionValueId, v => v.Id, (l, v) => v)
            .Join(db.OptionTypes, v => v.OptionTypeId, t => t.Id, (v, t) => new { v.Id, t.Position })
            .OrderBy(x => x.Position).Select(x => x.Id).ToListAsync(ct));

    private static AdminSku ToProto(Sku s, IEnumerable<Guid> optionValueIds) => new()
    {
        Id = s.Id.ToString(), Code = s.Code, PriceCents = s.PriceCents, Active = s.Active, LaunchId = s.LaunchId?.ToString() ?? "", Version = s.Version,
        OptionValueIds = { optionValueIds.Select(v => v.ToString()) },
    };

    /// <summary>R14 / R15 checks on the whole option list; ids are resolved against the product by the caller.</summary>
    private static List<OptionTypeInput> OptionInputs(IEnumerable<V1.OptionType> input)
    {
        static Guid? Id(string raw, string what) => raw == "" ? null : Guid.TryParse(raw, out var id) ? id : throw CatalogErrors.NotFoundError(what);
        var wanted = input.Select(t => new OptionTypeInput(Id(t.Id, "Option type"), Inputs.Name(t.Name, "option_types", 50),
            t.Values.Select(v => new OptionValueInput(Id(v.Id, "Option value"), Inputs.Name(v.Value, "option_types", 50))).ToList())).ToList();

        var ids = wanted.Select(t => t.Id).Concat(wanted.SelectMany(t => t.Values).Select(v => v.Id)).OfType<Guid>().ToList();
        if (wanted.Any(t => t.Values.Count == 0)
            || wanted.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != wanted.Count
            || wanted.Any(t => t.Values.Select(v => v.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != t.Values.Count)
            || ids.Distinct().Count() != ids.Count)
            throw CatalogErrors.Validation("option_types", "Each option type needs a unique name and at least one value; values must be unique within a type.");
        return wanted;
    }

    private static RpcException SkuCodeTaken() => CatalogErrors.Error(CatalogErrors.SkuCodeTaken, "This SKU code is already in use.");

    private static RpcException DuplicateCombination() =>
        CatalogErrors.Error(CatalogErrors.DuplicateCombination, "A SKU with this option combination already exists.");
}
