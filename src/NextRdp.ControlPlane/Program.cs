using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.OpenApi;
using NextRdp.ControlPlane;
using NextRdp.Platform.Application;
using NextRdp.Platform.Infrastructure;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PlatformProblemExceptionHandler>();
builder.Services.AddOpenApi(options => options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "NextRdp-Csrf-Development" : "__Host-NextRdp-Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("nextrdp_user_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 300,
                QueueLimit = 0,
                TokensPerPeriod = 300,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));
});

builder.Services.AddNextRdpPlatformPersistence(builder.Configuration);
builder.Services.AddNextRdpAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddNextRdpAuthorization(builder.Configuration);
builder.Services.AddNextRdpPlatformServices(builder.Configuration, builder.Environment);
builder.Services.AddNextRdpHealthChecks();

var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("NextRdp.ControlPlane"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation());
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
if (Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var otlpUri)) telemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, otlpUri);

var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
}).AllowAnonymous();
app.MapAuthenticationEndpoints();
app.MapNextRdpApi();

await app.InitializePlatformAsync();
await app.RunAsync();

public partial class Program;
