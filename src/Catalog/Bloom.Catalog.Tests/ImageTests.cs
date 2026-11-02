using System.Net;
using System.Text.Json;
using Amazon.S3;
using Bloom.Catalog.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Catalog.Tests;

public class ImageTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> CreateAsync(TestCatalog t, Guid productId, string contentType, object? skuId = null, string? altText = "front") =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/products/{productId}/images", new { contentType, altText, skuId }, Manager);

    private static Task<HttpResponseMessage> ConfirmAsync(TestCatalog t, string imageId) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/images/{imageId}/confirm", caller: Manager);

    private static async Task<HttpResponseMessage> PutObjectAsync(string url, int bytes, string contentType)
    {
        using var http = new HttpClient();
        using var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new(contentType);
        return await http.PutAsync(url, content, TestContext.Current.CancellationToken);
    }

    /// <summary>Creates an upload, puts <paramref name="bytes"/> into storage and returns (imageId, key).</summary>
    private static async Task<(string Id, string Key)> UploadAsync(TestCatalog t, Guid productId, int bytes = 10_240)
    {
        var created = await (await CreateAsync(t, productId, "image/png")).OkJsonAsync();
        var put = await PutObjectAsync(created.GetProperty("uploadUrl").GetString()!, bytes, "image/png");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return (created.GetProperty("imageId").GetString()!, created.GetProperty("storageKey").GetString()!);
    }

    private async Task<bool> ObjectExistsAsync(TestCatalog t, string key)
    {
        try
        {
            await containers.S3.GetObjectMetadataAsync(t.Bucket, key, TestContext.Current.CancellationToken);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    private static async Task<JsonElement> GetAdminAsync(TestCatalog t, Guid productId) =>
        await (await t.SendAsync(HttpMethod.Get, $"{Admin}/products/{productId}", caller: Manager)).OkJsonAsync();

    private static async Task<JsonElement> GetPageAsync(TestCatalog t, Guid productId) =>
        await (await t.SendAsync(HttpMethod.Get, $"/api/catalog/products/{productId}")).OkJsonAsync();

    [Fact]
    public async Task Upload_confirm_shows_image()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, skus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        Assert.Equal(0, (await GetPageAsync(t, product.Id)).GetProperty("images").GetArrayLength()); // caches the page without images

        var created = await (await CreateAsync(t, product.Id, "image/png", skus[0].Id)).OkJsonAsync();
        var url = created.GetProperty("uploadUrl").GetString()!;
        var key = created.GetProperty("storageKey").GetString()!;
        // 300 s = 5 min. The SDK rounds (Expires - its per-endpoint corrected clock) to whole seconds and MinIO's Date header has second
        // resolution, so the value is 299 to 301 from run to run; there is no deterministic way to pin it short of disabling skew correction.
        var expires = int.Parse(System.Text.RegularExpressions.Regex.Match(url, @"X-Amz-Expires=(\d+)").Groups[1].Value);
        Assert.InRange(expires, 299, 301);
        Assert.Matches($"^products/{product.Id}/[0-9a-f-]{{36}}\\.png$", key);
        Assert.Equal(HttpStatusCode.OK, (await PutObjectAsync(url, 10_240, "image/png")).StatusCode);

        var confirmed = await (await ConfirmAsync(t, created.GetProperty("imageId").GetString()!)).OkJsonAsync();

        Assert.Equal("ready", confirmed.GetProperty("status").GetString());
        Assert.Equal($"{containers.MinioUrl}/{t.Bucket}/{key}", confirmed.GetProperty("url").GetString());
        Assert.Equal(skus[0].Id.ToString(), confirmed.GetProperty("skuId").GetString());
        Assert.Equal("front", confirmed.GetProperty("altText").GetString());
        var shown = (await GetPageAsync(t, product.Id)).GetProperty("images");
        Assert.Equal(created.GetProperty("imageId").GetString(), Assert.Single(shown.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(["catalog.image-upload-created", "catalog.image-confirmed"], (await Outbox.AuditAsync(t)).Select(e => e.Action));
        Assert.All(await Outbox.AuditAsync(t), e => { Assert.Equal("image", e.TargetType); Assert.Equal(created.GetProperty("imageId").GetString(), e.TargetId); });
    }

    [Fact]
    public async Task Confirming_a_ready_image_again_writes_nothing()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var (id, _) = await UploadAsync(t, product.Id);
        await (await ConfirmAsync(t, id)).OkJsonAsync();

        Assert.Equal("ready", (await (await ConfirmAsync(t, id)).OkJsonAsync()).GetProperty("status").GetString());

        Assert.Equal(2, (await Outbox.AuditAsync(t)).Count);
    }

    [Fact]
    public async Task C13_six_mb_upload_is_IMAGE_INVALID()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var (id, key) = await UploadAsync(t, product.Id, 6 * 1024 * 1024);

        await (await ConfirmAsync(t, id)).ExpectErrorAsync(400, "IMAGE_INVALID");

        Assert.False(await ObjectExistsAsync(t, key));
        await using var db = t.NewDb();
        Assert.Equal("pending", (await db.ProductImages.SingleAsync(i => i.Id == Guid.Parse(id), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(["catalog.image-upload-created"], (await Outbox.AuditAsync(t)).Select(e => e.Action));
    }

    [Fact]
    public async Task Confirm_without_upload_is_IMAGE_INVALID()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var created = await (await CreateAsync(t, product.Id, "image/webp")).OkJsonAsync();

        await (await ConfirmAsync(t, created.GetProperty("imageId").GetString()!)).ExpectErrorAsync(400, "IMAGE_INVALID");
        await (await ConfirmAsync(t, Guid.NewGuid().ToString())).ExpectErrorAsync(404, "NOT_FOUND");

        await using var db = t.NewDb();
        Assert.Equal("pending", await db.ProductImages.Select(i => i.Status).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Wrong_content_type_is_IMAGE_INVALID()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, skus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var (other, otherSkus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);

        await (await CreateAsync(t, product.Id, "image/gif")).ExpectErrorAsync(400, "IMAGE_INVALID");
        await (await CreateAsync(t, product.Id, "image/png", otherSkus[0].Id)).ExpectErrorAsync(404, "NOT_FOUND"); // another product's SKU
        await (await CreateAsync(t, Guid.NewGuid(), "image/png")).ExpectErrorAsync(404, "NOT_FOUND");
        var tooLong = await (await CreateAsync(t, product.Id, "image/png", altText: new string('a', 301))).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("alt_text", tooLong.GetProperty("metadata").GetProperty("field").GetString());
        await (await t.SendAsync(HttpMethod.Post, $"{Admin}/products/{product.Id}/images", new { contentType = "image/png" }, TestCaller.Staff("support")))
            .ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        await using var db = t.NewDb();
        Assert.Empty(await db.ProductImages.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Upload_with_other_content_type_is_refused_by_storage()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var created = await (await CreateAsync(t, product.Id, "image/png")).OkJsonAsync();

        var put = await PutObjectAsync(created.GetProperty("uploadUrl").GetString()!, 1_024, "image/jpeg");

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Reorder_and_delete()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var uploads = new List<(string Id, string Key)>();
        for (var i = 0; i < 3; i++)
        {
            uploads.Add(await UploadAsync(t, product.Id));
            if (i < 2) await (await ConfirmAsync(t, uploads[i].Id)).OkJsonAsync(); // the third stays pending
        }
        var order = $"{Admin}/products/{product.Id}/images/order";
        string[] Ids(JsonElement p) => p.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(uploads.Select(u => u.Id), Ids(await GetAdminAsync(t, product.Id))); // position = max + 1

        var reordered = await (await t.SendAsync(HttpMethod.Put, order, new { imageIds = new[] { uploads[2].Id, uploads[0].Id, uploads[1].Id } }, Manager)).OkJsonAsync();

        Assert.Equal([uploads[2].Id, uploads[0].Id, uploads[1].Id], Ids(reordered));
        Assert.Equal([uploads[0].Id, uploads[1].Id], (await GetPageAsync(t, product.Id)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        foreach (var incomplete in new[] { new[] { uploads[0].Id }, [uploads[0].Id, uploads[1].Id, uploads[2].Id, uploads[2].Id], [uploads[0].Id, uploads[1].Id, Guid.NewGuid().ToString()] })
        {
            var info = await (await t.SendAsync(HttpMethod.Put, order, new { imageIds = incomplete }, Manager)).ExpectErrorAsync(400, "VALIDATION_FAILED");
            Assert.Equal("image_ids", info.GetProperty("metadata").GetProperty("field").GetString());
        }

        var ready = uploads[0];
        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Delete, $"{Admin}/images/{ready.Id}", caller: Manager)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Delete, $"{Admin}/images/{uploads[2].Id}", caller: Manager)).StatusCode);
        await (await t.SendAsync(HttpMethod.Delete, $"{Admin}/images/{ready.Id}", caller: Manager)).ExpectErrorAsync(404, "NOT_FOUND");

        Assert.Equal([uploads[1].Id], Ids(await GetAdminAsync(t, product.Id)));
        Assert.Equal([uploads[1].Id], (await GetPageAsync(t, product.Id)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.False(await ObjectExistsAsync(t, ready.Key));
        Assert.False(await ObjectExistsAsync(t, uploads[2].Key));
        Assert.True(await ObjectExistsAsync(t, uploads[1].Key));
        var actions = (await Outbox.AuditAsync(t)).GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["catalog.image-upload-created"] = 3, ["catalog.image-confirmed"] = 2, ["catalog.images-reordered"] = 1, ["catalog.image-deleted"] = 2,
        }, actions); // failed and unchanged calls wrote nothing
    }

    [Fact]
    public async Task Unconfirmed_upload_is_cleaned_after_24_hours()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var (ready, readyKey) = await UploadAsync(t, product.Id);
        await (await ConfirmAsync(t, ready)).OkJsonAsync();
        var (pending, pendingKey) = await UploadAsync(t, product.Id);
        var job = ActivatorUtilities.CreateInstance<ImageCleanupJob>(t.Services);

        t.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await job.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.True(await ObjectExistsAsync(t, pendingKey));

        t.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await job.RunOnceAsync(TestContext.Current.CancellationToken));

        Assert.False(await ObjectExistsAsync(t, pendingKey));
        Assert.True(await ObjectExistsAsync(t, readyKey)); // ready images are untouched
        await using var db = t.NewDb();
        Assert.Equal([ready], (await db.ProductImages.ToListAsync(TestContext.Current.CancellationToken)).Select(i => i.Id.ToString()));
        Assert.DoesNotContain(pending, (await db.ProductImages.ToListAsync(TestContext.Current.CancellationToken)).Select(i => i.Id.ToString()));
    }
}
