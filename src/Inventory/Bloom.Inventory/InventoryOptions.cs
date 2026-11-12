namespace Bloom.Inventory;

/// <summary>Section <c>Inventory</c> (plan R4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class InventoryOptions
{
    public string Mode { get; set; } = "";
    /// <summary>Connection string of the app role. Migrations run as the database owner, outside the app (plan R2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    /// <summary>Absolute URL of catalog's gRPC endpoint.</summary>
    public string Catalog { get; set; } = "";

    public bool IsApi => Mode == "api";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Inventory:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Inventory:Database", Database);
        Require("Inventory:RabbitMq", RabbitMq);
        Require("Inventory:Catalog", Catalog);
        if (!Uri.TryCreate(Catalog, UriKind.Absolute, out _)) throw Bad("Inventory:Catalog", "must be an absolute URL");
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }
}
