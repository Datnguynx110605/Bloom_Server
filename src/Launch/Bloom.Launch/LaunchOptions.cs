using StackExchange.Redis;

namespace Bloom.Launch;

/// <summary>Section <c>Launch</c> (plan R4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class LaunchOptions
{
    public const int MinPassKeyBytes = 32;

    public string Mode { get; set; } = "";
    /// <summary>Connection string of the database owner: launch has no restricted role (plan R2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    public string Redis { get; set; } = "";
    /// <summary>Absolute URL of catalog's gRPC API.</summary>
    public string Catalog { get; set; } = "";
    /// <summary>The HMAC keys that verify admission passes (01-launch §4.3); the one named by <see cref="CurrentPassKid"/> signs.</summary>
    public List<PassKey> PassKeys { get; set; } = [];
    public string CurrentPassKid { get; set; } = "";
    /// <summary>Cap on <c>admission_rate</c> (passes admitted per tick) of a launch.</summary>
    public int AdmissionCeiling { get; set; } = 200;

    public sealed class PassKey
    {
        public string Kid { get; set; } = "";
        /// <summary>Base64 of at least <see cref="MinPassKeyBytes"/> random bytes.</summary>
        public string Key { get; set; } = "";
    }

    public bool IsApi => Mode == "api";

    /// <summary>The decoded <see cref="PassKeys"/> by kid; set by <see cref="Validate"/>.</summary>
    public IReadOnlyDictionary<string, byte[]> DecodedKeys { get; private set; } = new Dictionary<string, byte[]>();

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key. A message never contains a pass key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Launch:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Launch:Database", Database);
        Require("Launch:RabbitMq", RabbitMq);
        Require("Launch:Redis", Redis);
        try
        {
            if (ConfigurationOptions.Parse(Redis).EndPoints.Count == 0) throw new ArgumentException("no endpoint");
        }
        catch (ArgumentException)
        {
            throw Bad("Launch:Redis", "is not a valid Redis configuration string"); // never the value: it may hold a password
        }
        Require("Launch:Catalog", Catalog);
        if (!Uri.TryCreate(Catalog, UriKind.Absolute, out _)) throw Bad("Launch:Catalog", "must be an absolute URL");
        if (PassKeys.Count == 0) throw Bad("Launch:PassKeys", "needs at least one key");

        var decoded = new Dictionary<string, byte[]>();
        for (var i = 0; i < PassKeys.Count; i++)
        {
            var (kid, key) = (PassKeys[i].Kid, PassKeys[i].Key);
            Require($"Launch:PassKeys:{i}:Kid", kid);
            Require($"Launch:PassKeys:{i}:Key", key);
            var bytes = new byte[key.Length];
            if (!Convert.TryFromBase64String(key, bytes, out var n)) throw Bad($"Launch:PassKeys:{i}:Key", "must be base64");
            if (n < MinPassKeyBytes) throw Bad($"Launch:PassKeys:{i}:Key", $"must be at least {MinPassKeyBytes} bytes");
            if (!decoded.TryAdd(kid, bytes[..n])) throw Bad($"Launch:PassKeys:{i}:Kid", "is listed twice");
        }

        Require("Launch:CurrentPassKid", CurrentPassKid);
        if (!decoded.ContainsKey(CurrentPassKid)) throw Bad("Launch:CurrentPassKid", "must be the Kid of one of Launch:PassKeys");
        if (AdmissionCeiling < 1) throw Bad("Launch:AdmissionCeiling", "must be at least 1");
        DecodedKeys = decoded;
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }
}
