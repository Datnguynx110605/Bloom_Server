namespace Bloom.Launch.WaitingRoom;

/// <summary>The Redis keys of 01-launch §4.2. <c>{L}</c> is a literal hash tag, so one launch's keys share a slot and the admission script is atomic.</summary>
public static class RedisKeys
{
    public static string Queue(Guid launch) => $"wr:{{{launch:D}}}:q";

    /// <summary>The prefix of every <see cref="Pass"/> key of the launch; the admission script appends the account.</summary>
    public static string PassPrefix(Guid launch) => $"wr:{{{launch:D}}}:pass:";

    public static string Pass(Guid launch, Guid account) => $"{PassPrefix(launch)}{account:D}";

    public static string Passes(Guid launch) => $"wr:{{{launch:D}}}:passes";

    public static string State(Guid launch) => $"wr:{{{launch:D}}}:state";

    public static string Tick(Guid launch) => $"wr:{{{launch:D}}}:tick";
}
