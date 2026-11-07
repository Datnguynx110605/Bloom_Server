namespace Bloom.Order;

/// <summary>Section <c>Order</c> (plan R4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class OrderOptions
{
    public string Mode { get; set; } = "";
    /// <summary>Connection string of the database owner: order has no restricted role (plan R2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    /// <summary>Absolute URLs of the gRPC endpoints of the services order calls.</summary>
    public string Catalog { get; set; } = "";
    public string Inventory { get; set; } = "";
    public string Launch { get; set; } = "";
    public string Payment { get; set; } = "";
    public string Fulfillment { get; set; } = "";
    /// <summary>Flat sales-tax rate in basis points (825 = 8.25 %), 0 to 10000; null when the key is missing.</summary>
    public int? TaxRateBasisPoints { get; set; }

    public bool IsApi => Mode == "api";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Order:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Order:Database", Database);
        Require("Order:RabbitMq", RabbitMq);
        Url("Order:Catalog", Catalog);
        Url("Order:Inventory", Inventory);
        Url("Order:Launch", Launch);
        Url("Order:Payment", Payment);
        Url("Order:Fulfillment", Fulfillment);
        if (TaxRateBasisPoints is not (>= 0 and <= 10000)) throw Bad("Order:TaxRateBasisPoints", "is required and must be 0 to 10000");
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }

    private static void Url(string key, string value)
    {
        Require(key, value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out _)) throw Bad(key, "must be an absolute URL");
    }
}
