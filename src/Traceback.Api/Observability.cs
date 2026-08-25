using System.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Traceback.Api;

/// <summary>
/// Traceback observes itself before it observes other systems: traces and
/// metrics around ingestion, persistence, and query execution are emitted from
/// the first commit. OTLP export activates when OTEL_EXPORTER_OTLP_ENDPOINT is
/// configured.
/// </summary>
public static class Observability
{
    public const string ServiceName = "traceback.api";

    /// <summary>All custom activity sources, for registration with OTel.</summary>
    public static readonly string[] ActivitySourceNames =
    [
        "Traceback.Ingestion",
        "Traceback.Queries",
        "Traceback.Sync",
    ];

    public static WebApplicationBuilder ConfigureObservability(this WebApplicationBuilder builder)
    {
        var otlpBuilder = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(ServiceName, serviceInstanceId: Environment.MachineName));

        otlpBuilder.WithTracing(tracing =>
        {
            tracing
                .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/healthz");
                })
                .AddHttpClientInstrumentation()
                .AddSource(ActivitySourceNames);

            if (HasOtlpEndpoint(builder))
                tracing.AddOtlpExporter();
        });

        otlpBuilder.WithMetrics(metrics =>
        {
            metrics
                .AddRuntimeInstrumentation()
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("Traceback.Ingestion")
                .AddMeter("Traceback.Queries")
                .AddMeter("Traceback.Sync");

            if (HasOtlpEndpoint(builder))
                metrics.AddOtlpExporter();
        });

        return builder;
    }

    private static bool HasOtlpEndpoint(WebApplicationBuilder builder) =>
        !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? builder.Configuration["Otel:OtlpEndpoint"]);
}
