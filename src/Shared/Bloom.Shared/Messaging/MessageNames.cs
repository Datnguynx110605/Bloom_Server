using System.Text.RegularExpressions;

namespace Bloom.Shared.Messaging;

public static class MessageKinds
{
    public const string Event = "event";
    public const string Command = "command";
}

public static partial class MessageNames
{
    public static IReadOnlyList<(string Delay, int Milliseconds)> RetryDelays { get; } =
        [("1s", 1000), ("5s", 5000), ("30s", 30000), ("2m", 120000)];

    public static string EventExchange(string producer) => $"{producer}.events";

    public static string CommandExchange(string target) => $"{target}.commands";

    public static string ConsumerQueue(string service, string handler) => $"{service}.{handler}";

    public static string RetryQueue(string queue, string delay) => $"{queue}.retry.{delay}";

    public static string DeadLetterQueue(string queue) => $"{queue}.dlq";

    public static bool IsValidMessageType(string messageType) => MessageTypeRegex().IsMatch(messageType);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z0-9]+(-[a-z0-9]+)*)+\.v[1-9][0-9]*\z")]
    private static partial Regex MessageTypeRegex();
}
