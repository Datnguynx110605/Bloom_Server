using System.Diagnostics;

namespace Bloom.Shared.Observability;

internal static class BloomActivity
{
    public static readonly ActivitySource Source = new("Bloom.Shared");
}
