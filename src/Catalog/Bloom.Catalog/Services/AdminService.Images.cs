using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Image = Bloom.Catalog.V1.Image;

namespace Bloom.Catalog.Services;

public sealed partial class AdminService
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> ImageExtensions = new()
    {
        ["image/jpeg"] = "jpg", ["image/png"] = "png", ["image/webp"] = "webp",
    };

    public override async Task<CreateImageUploadResponse> CreateImageUpload(CreateImageUploadRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        if (!ImageExtensions.TryGetValue(request.ContentType, out var extension))
            throw CatalogErrors.Error(CatalogErrors.ImageInvalid, "content_type must be image/jpeg, image/png or image/webp.");
        var altText = Inputs.OptionalText(request.AltText, "alt_text", 300);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Product");
        Guid? skuId = request.SkuId == "" ? null : Guid.TryParse(request.SkuId, out var parsed) ? parsed : throw CatalogErrors.NotFoundError("SKU");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockProductAsync(productId, ct); // serializes the position (max + 1) of concurrent uploads
        if (skuId is { } sku && !await db.Skus.AnyAsync(s => s.Id == sku && s.ProductId == productId, ct)) throw CatalogErrors.NotFoundError("SKU");
        var row = new ProductImage
        {
            Id = Guid.CreateVersion7(), ProductId = productId, SkuId = skuId, AltText = altText, CreatedAt = clock.GetUtcNow(),
            StorageKey = $"products/{productId}/{Guid.NewGuid():D}.{extension}",
            Position = (await db.ProductImages.Where(i => i.ProductId == productId).MaxAsync(i => (int?)i.Position, ct) ?? 0) + 1,
        };
        db.ProductImages.Add(row);
        audit.Record(caller, "catalog.image-upload-created", "image", row.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CreateImageUploadResponse { ImageId = row.Id.ToString(), UploadUrl = imageStore.PresignPut(row.StorageKey, request.ContentType), StorageKey = row.StorageKey };
    }

    public override async Task<Image> ConfirmImage(ImageRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var ct = context.CancellationToken;
        var image = await FindImageAsync(request.Id, ct);
        if (image.Status == ImageStatuses.Ready) return ImageToProto(image);

        // Storage can't cap a presigned PUT, so the 5 MB limit is checked here (spec §7): too big or missing → object gone, row stays pending.
        if (await imageStore.SizeAsync(image.StorageKey) is not { } size || size > MaxImageBytes)
        {
            await imageStore.DeleteAsync(image.StorageKey);
            throw CatalogErrors.Error(CatalogErrors.ImageInvalid, "The uploaded file is missing or larger than 5 MB.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var confirmed = await db.ProductImages.Where(i => i.Id == image.Id && i.Status == ImageStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ImageStatuses.Ready), ct);
        if (confirmed == 0) return ImageToProto(await FindImageAsync(request.Id, ct)); // a parallel confirm won: nothing to write
        audit.Record(caller, "catalog.image-confirmed", "image", image.Id);
        await InvalidateProductAsync(image.ProductId, isPlan: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        image.Status = ImageStatuses.Ready;
        return ImageToProto(image);
    }

    public override async Task<AdminProduct> ReorderImages(ReorderImagesRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Product");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockProductAsync(productId, ct);
        var images = await db.ProductImages.Where(i => i.ProductId == productId).ToListAsync(ct);
        var wanted = request.ImageIds.Select(raw => Guid.TryParse(raw, out var id) ? id : (Guid?)null).ToList();
        if (wanted.Count != images.Count || wanted.Distinct().Count() != wanted.Count || wanted.Any(w => w is null || images.All(i => i.Id != w)))
            throw CatalogErrors.Validation("image_ids", "image_ids must list every image of the product exactly once.");

        var changed = false;
        for (var i = 0; i < wanted.Count; i++)
        {
            var image = images.Single(x => x.Id == wanted[i]);
            if (image.Position == i + 1) continue;
            image.Position = i + 1;
            changed = true;
        }
        if (!changed) return await LoadProductAsync(productId, ct); // no write, no audit (R13)
        audit.Record(caller, "catalog.images-reordered", "product", productId);
        await InvalidateProductAsync(productId, isPlan: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(productId, ct);
    }

    public override async Task<Empty> DeleteImage(ImageRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var ct = context.CancellationToken;
        var image = await FindImageAsync(request.Id, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.ProductImages.Where(i => i.Id == image.Id).ExecuteDeleteAsync(ct) == 0) throw CatalogErrors.NotFoundError("Image");
        audit.Record(caller, "catalog.image-deleted", "image", image.Id);
        if (image.Status == ImageStatuses.Ready) await InvalidateProductAsync(image.ProductId, isPlan: false, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await imageStore.DeleteAsync(image.StorageKey); // after the row: a failure leaves an unreferenced object, never a ready image without one
        return new Empty();
    }

    private async Task<ProductImage> FindImageAsync(string rawId, CancellationToken ct) =>
        Guid.TryParse(rawId, out var id) ? await db.ProductImages.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw CatalogErrors.NotFoundError("Image")
            : throw CatalogErrors.NotFoundError("Image");

    private Image ImageToProto(ProductImage i) => new()
    {
        Id = i.Id.ToString(), Url = imageStore.PublicUrl(i.StorageKey), AltText = i.AltText ?? "", SkuId = i.SkuId?.ToString() ?? "",
        Position = i.Position, Status = i.Status,
    };
}
