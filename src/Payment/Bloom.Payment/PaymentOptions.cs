namespace Bloom.Payment;

/// <summary>Section <c>Payment</c> (plan R4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class PaymentOptions
{
    public string Mode { get; set; } = "";
    /// <summary>Connection string of the database owner: payment has no restricted role (plan R2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    /// <summary>Absolute URL of the storefront, where Stripe sends the customer back.</summary>
    public string StorefrontBaseUrl { get; set; } = "";
    public StripeOptions Stripe { get; set; } = new();

    public sealed class StripeOptions
    {
        public string ApiKey { get; set; } = "";
        public string WebhookSecret { get; set; } = "";
        /// <summary>Absolute URL of the Stripe API; tests point it at the fake.</summary>
        public string BaseUrl { get; set; } = "";
    }

    public bool IsApi => Mode == "api";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Payment:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Payment:Database", Database);
        Require("Payment:RabbitMq", RabbitMq);
        Url("Payment:StorefrontBaseUrl", StorefrontBaseUrl);
        Require("Payment:Stripe:ApiKey", Stripe.ApiKey);
        Require("Payment:Stripe:WebhookSecret", Stripe.WebhookSecret);
        Url("Payment:Stripe:BaseUrl", Stripe.BaseUrl);
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }

    private static void Url(string key, string value)
    {
        Require(key, value);
        // P-6: on Unix "/v1" parses as the absolute URL file:///v1, so the scheme is checked too.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw Bad(key, "must be an absolute http or https URL");
    }
}
