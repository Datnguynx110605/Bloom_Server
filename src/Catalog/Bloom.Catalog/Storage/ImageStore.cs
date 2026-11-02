using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Bloom.Catalog.Storage;

/// <summary>Product image objects in the bucket (plan R18).</summary>
public sealed class ImageStore(CatalogOptions options, IAmazonS3 s3)
{
    public string PublicUrl(string key) => $"{options.Storage.PublicBaseUrl}/{key}";

    /// <summary>A <c>PUT</c> URL for exactly this content type, valid 5 minutes. The credential lifetime uses the real clock, not the business clock.</summary>
    public string PresignPut(string key, string contentType) => s3.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = options.Storage.Bucket,
        Key = key,
        Verb = HttpVerb.PUT,
        ContentType = contentType,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Protocol = options.Storage.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
    });

    /// <summary>Object size in bytes, null when the object does not exist.</summary>
    public async Task<long?> SizeAsync(string key)
    {
        try
        {
            return (await s3.GetObjectMetadataAsync(options.Storage.Bucket, key)).ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Deletes the object; a missing object is not an error (S3 semantics).</summary>
    public Task DeleteAsync(string key) => s3.DeleteObjectAsync(options.Storage.Bucket, key);
}
