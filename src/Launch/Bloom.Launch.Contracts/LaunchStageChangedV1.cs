namespace Bloom.Launch.Contracts;

/// <summary>Published by launch on every change of stage, products or SKUs (01-launch §5.3); consumed by catalog (08-catalog §6.4).</summary>
public sealed record LaunchStageChangedV1(
    Guid LaunchId, string Stage, DateTimeOffset LaunchTime, DateTimeOffset ReleaseDate,
    IReadOnlyList<LaunchProductV1> Products, IReadOnlyList<LaunchSkuV1> Skus);

public sealed record LaunchProductV1(Guid ProductId, int PurchaseLimit);

public sealed record LaunchSkuV1(Guid SkuId, Guid ProductId);
