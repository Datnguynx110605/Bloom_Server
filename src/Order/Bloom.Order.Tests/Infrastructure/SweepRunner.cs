using Bloom.Order.Observability;
using Bloom.Order.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bloom.Order.Tests;

public static class SweepRunner
{
    /// <summary>The sweeps on the host's scopes, clock and metrics; tests drive them through <c>Run…Async</c> (plan R16), the api host does not run them.</summary>
    public static Sweeps Of(TestOrder t) =>
        new(t.Services.GetRequiredService<IServiceScopeFactory>(), t.Clock, t.Services.GetRequiredService<OrderMetrics>(), NullLogger<Sweeps>.Instance);
}
