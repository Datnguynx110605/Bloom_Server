namespace Bloom.Catalog.Storage;

/// <summary>Product image objects in the bucket (plan R18).</summary>
public sealed class ImageStore(CatalogOptions options)
{
    public string PublicUrl(string key) => $"{options.Storage.PublicBaseUrl}/{key}";
}
