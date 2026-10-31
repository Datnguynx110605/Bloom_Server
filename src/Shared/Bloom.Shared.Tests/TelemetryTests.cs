using System.Diagnostics;
using Bloom.Shared.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Bloom.Shared.Tests;

public class TelemetryTests
{
    [Fact]
    public void AddBloomTelemetry_registers_providers()
    {
        var exported = new List<Activity>();
        var builder = Host.CreateApplicationBuilder();
        builder.AddBloomTelemetry("svc");
        builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
        using var host = builder.Build();

        var tracer = host.Services.GetRequiredService<TracerProvider>();
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
        BloomActivity.Source.StartActivity("probe")?.Dispose();
        tracer.ForceFlush();

        Assert.Contains(exported, a => a.OperationName == "probe");
        Assert.Contains(tracer.GetResource().Attributes, a => a.Key == "service.name" && Equals(a.Value, "svc"));
    }
}
