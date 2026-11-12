namespace Bloom.Catalog;

public sealed class StorageOptions
{
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "bloom-product-images";
    public string Region { get; set; } = "us-east-1";
    public string PublicBaseUrl { get; set; } = "";
}

/// <summary>Section <c>Catalog</c> (plan R4). Both modes need every key: the worker invalidates the cache and deletes objects.</summary>
public sealed class CatalogOptions
{
    public string Mode { get; set; } = "";
    public string Database { get; set; } = "";
    public string Redis { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    public StorageOptions Storage { get; set; } = new();

    public bool IsApi => Mode == "api";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Catalog:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Catalog:Database", Database);
        Require("Catalog:Redis", Redis);
        Require("Catalog:RabbitMq", RabbitMq);
        RequireUrl("Catalog:Storage:ServiceUrl", Storage.ServiceUrl);
        Require("Catalog:Storage:AccessKey", Storage.AccessKey);
        Require("Catalog:Storage:SecretKey", Storage.SecretKey);
        Require("Catalog:Storage:Bucket", Storage.Bucket);
        Require("Catalog:Storage:Region", Storage.Region);
        RequireUrl("Catalog:Storage:PublicBaseUrl", Storage.PublicBaseUrl);
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }

    private static void RequireUrl(string key, string value)
    {
        Require(key, value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out _)) throw Bad(key, "must be an absolute URL");
    }
}
