namespace Bloom.Launch.Contracts;

/// <summary>Command to launch: release the allocation hold of an order (01-launch §5.3).</summary>
public sealed record ReleaseAllocationHoldV1(Guid OrderId, string Reason);

/// <summary>Published by launch when the hold is released (01-launch §5.3).</summary>
public sealed record HoldReleasedV1(Guid OrderId);
