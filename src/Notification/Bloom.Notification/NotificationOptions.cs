using System.Net.Mail;

namespace Bloom.Notification;

/// <summary>Section <c>Notification</c> (plan N4). Both modes need every key: the DLQ admin of the API needs the same consumers as the worker.</summary>
public sealed class NotificationOptions
{
    public string Mode { get; set; } = "";
    /// <summary>Connection string of the database owner: notification has no restricted role (plan N2).</summary>
    public string Database { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    public string Redis { get; set; } = "";
    /// <summary>Absolute URL of the storefront, the base of the links in notifications and emails.</summary>
    public string StorefrontBaseUrl { get; set; } = "";
    /// <summary>Base64 of the 32-byte key that encrypts verification codes at rest (NR-03).</summary>
    public string CodeEncryptionKey { get; set; } = "";
    /// <summary>Origins allowed to open the hub, each <c>scheme://host[:port]</c>.</summary>
    public string[] AllowedOrigins { get; set; } = [];
    public EmailOptions Email { get; set; } = new();

    public sealed class EmailOptions
    {
        /// <summary><c>resend</c> or <c>smtp</c>.</summary>
        public string Provider { get; set; } = "";
        public string From { get; set; } = "";
        public ResendOptions Resend { get; set; } = new();
        public SmtpOptions Smtp { get; set; } = new();
    }

    public sealed class ResendOptions
    {
        public string ApiKey { get; set; } = "";
        /// <summary>Absolute URL of the Resend API; tests point it at the fake.</summary>
        public string BaseUrl { get; set; } = "";
    }

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public bool IsApi => Mode == "api";

    /// <summary><see cref="CodeEncryptionKey"/> decoded; set by <see cref="Validate"/>.</summary>
    public byte[] CodeKey { get; private set; } = [];

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Notification:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Notification:Database", Database);
        Require("Notification:RabbitMq", RabbitMq);
        Require("Notification:Redis", Redis);
        Url("Notification:StorefrontBaseUrl", StorefrontBaseUrl);
        CodeKey = DecodeKey();
        if (AllowedOrigins.Length == 0) throw Bad("Notification:AllowedOrigins", "is required");
        for (var i = 0; i < AllowedOrigins.Length; i++)
        {
            var origin = AllowedOrigins[i];
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw Bad($"Notification:AllowedOrigins:{i}", "must be an origin: scheme://host[:port]");
        }

        Require("Notification:Email:Provider", Email.Provider);
        Require("Notification:Email:From", Email.From);
        if (!MailAddress.TryCreate(Email.From, out _)) throw Bad("Notification:Email:From", "must be an email address, for example 'Bloom <no-reply@example.com>'"); // never echo the value
        switch (Email.Provider)
        {
            case "resend":
                Require("Notification:Email:Resend:ApiKey", Email.Resend.ApiKey);
                Url("Notification:Email:Resend:BaseUrl", Email.Resend.BaseUrl);
                break;
            case "smtp":
                Require("Notification:Email:Smtp:Host", Email.Smtp.Host);
                if (Email.Smtp.Port is < 1 or > 65535) throw Bad("Notification:Email:Smtp:Port", "must be between 1 and 65535");
                break;
            default:
                throw Bad("Notification:Email:Provider", $"must be 'resend' or 'smtp', not '{Email.Provider}'");
        }
    }

    private byte[] DecodeKey()
    {
        Require("Notification:CodeEncryptionKey", CodeEncryptionKey);
        try
        {
            var key = Convert.FromBase64String(CodeEncryptionKey);
            if (key.Length == 32) return key;
        }
        catch (FormatException) { }
        throw Bad("Notification:CodeEncryptionKey", "must be base64 of exactly 32 bytes"); // never echo the value: it is a secret
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
