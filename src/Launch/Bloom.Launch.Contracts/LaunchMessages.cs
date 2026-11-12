namespace Bloom.Launch.Contracts;

/// <summary>Message types of 01-launch §5.3.</summary>
public static class LaunchMessages
{
    public const string StageChanged = "launch.stage-changed.v1";
    public const string ReleaseAllocationHold = "launch.release-allocation-hold.v1";
    public const string HoldReleased = "launch.hold-released.v1";
}
