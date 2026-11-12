using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Bloom.Shared.Observability;

public static class BloomTelemetryExtensions
{
    /// <summary>
    /// Traces, metrics and logs over OTLP (HLD §9.3, HLD-ADR-015). The endpoint comes from the standard
    /// <c>OTEL_EXPORTER_OTLP_*</c> environment variables.
    /// </summary>
    public static IHostApplicationBuilder AddBloomTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
        });
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddGrpcClientInstrumentation()
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddNpgsql()
                .AddSource(BloomActivity.Source.Name))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(BloomMetrics.MeterName))
            .UseOtlpExporter();
        return builder;
    }
}
