namespace Bloom.Fulfillment;

/// <summary>Section <c>Fulfillment</c> (plan R4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class FulfillmentOptions
{
    public string Mode { get; set; } = "";
    /// <summary>Connection string of the database owner: fulfillment has no restricted role (plan R2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    /// <summary>Absolute URL of inventory's gRPC endpoint.</summary>
    public string Inventory { get; set; } = "";
    public ShippoOptions Shippo { get; set; } = new();
    public WarehouseOptions Warehouse { get; set; } = new();
    public ParcelOptions Parcel { get; set; } = new();

    public bool IsApi => Mode == "api";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Fulfillment:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Fulfillment:Database", Database);
        Require("Fulfillment:RabbitMq", RabbitMq);
        Require("Fulfillment:Inventory", Inventory);
        if (!Uri.TryCreate(Inventory, UriKind.Absolute, out _)) throw Bad("Fulfillment:Inventory", "must be an absolute URL");
        Require("Fulfillment:Shippo:ApiKey", Shippo.ApiKey);
        Require("Fulfillment:Shippo:WebhookSecret", Shippo.WebhookSecret);
        Require("Fulfillment:Shippo:Carrier", Shippo.Carrier);
        Require("Fulfillment:Shippo:StandardToken", Shippo.StandardToken);
        Require("Fulfillment:Shippo:ExpressToken", Shippo.ExpressToken);
        Require("Fulfillment:Warehouse:Name", Warehouse.Name);
        Require("Fulfillment:Warehouse:Street1", Warehouse.Street1);
        Require("Fulfillment:Warehouse:City", Warehouse.City);
        Require("Fulfillment:Warehouse:State", Warehouse.State);
        Require("Fulfillment:Warehouse:Zip", Warehouse.Zip);
        Require("Fulfillment:Warehouse:Phone", Warehouse.Phone);
        Positive("Fulfillment:Parcel:LengthIn", Parcel.LengthIn);
        Positive("Fulfillment:Parcel:WidthIn", Parcel.WidthIn);
        Positive("Fulfillment:Parcel:HeightIn", Parcel.HeightIn);
        Positive("Fulfillment:Parcel:WeightOz", Parcel.WeightOz);
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }

    private static void Positive(string key, decimal value)
    {
        if (value <= 0) throw Bad(key, "must be greater than 0");
    }
}

public sealed class ShippoOptions
{
    public string ApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    /// <summary>Carrier code for <c>tracks</c> (for example <c>usps</c>).</summary>
    public string Carrier { get; set; } = "";
    /// <summary>Shippo service-level token of the <c>standard</c> shipping option, also used for return and ship-back labels.</summary>
    public string StandardToken { get; set; } = "";
    public string ExpressToken { get; set; } = "";
}

/// <summary>Ship-from address and return destination of every label.</summary>
public sealed class WarehouseOptions
{
    public string Name { get; set; } = "";
    public string Street1 { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Zip { get; set; } = "";
    public string Phone { get; set; } = "";
}

/// <summary>The one default parcel of every label: inches and ounces.</summary>
public sealed class ParcelOptions
{
    public decimal LengthIn { get; set; }
    public decimal WidthIn { get; set; }
    public decimal HeightIn { get; set; }
    public decimal WeightOz { get; set; }
}
