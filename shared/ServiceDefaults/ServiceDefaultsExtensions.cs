using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

namespace ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public const string LiveHealthPath = "/health/live";
    public const string ReadyHealthPath = "/health/ready";

    internal const string LiveTag = "live";
    internal const string ReadyTag = "ready";
    internal const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureLogging();
        builder.ConfigureOpenTelemetry();

        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

        // Registered in every environment so the build can generate the OpenAPI document; only Development serves it.
        builder.Services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
        {
            // The default document calls decimals "double", and generated clients then read money as a double and lose cents.
            if ((Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type) == typeof(decimal))
            {
                schema.Format = "decimal";
            }

            return Task.CompletedTask;
        }));

        return builder;
    }

    /// <summary>Adds the custom meters of a service (by <see cref="System.Diagnostics.Metrics.Meter"/> name) to the OpenTelemetry metrics, so the OTLP exporter sends them.</summary>
    public static TBuilder AddMeters<TBuilder>(this TBuilder builder, params string[] meterNames)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(meterNames));

        return builder;
    }

    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapHealthChecks(LiveHealthPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
        });
        app.MapHealthChecks(ReadyHealthPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
        });

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }

    private static void ConfigureLogging<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
    }

    private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !IsHealthRequest(context.Request.Path))
                .AddHttpClientInstrumentation())
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }
    }

    private static bool IsHealthRequest(PathString path) =>
        path.StartsWithSegments(LiveHealthPath) || path.StartsWithSegments(ReadyHealthPath);
}
