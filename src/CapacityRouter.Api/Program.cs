using Azure.Monitor.OpenTelemetry.AspNetCore;
using CapacityRouter.Api;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMetrics();
builder.Services.AddSingleton<RouterMetrics>();
builder.Services.AddSingleton<CapacityRegistry>();
builder.Services.Configure<HeartbeatOptions>(builder.Configuration.GetSection(HeartbeatOptions.Section));
builder.Services.AddHostedService<HeartbeatMonitor>();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

// Telemetry: requests, dependencies, logs and custom metrics go to Application Insights
// when a connection string is configured (it is, in Azure, via infra/main.bicep).
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor()
        .WithMetrics(metrics => metrics.AddMeter(RouterMetrics.MeterName));
}

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health");

app.MapGet("/", () => Results.Ok(new { service = "capacity-router", status = "running" }));

// Register (or resize) a capacity.
app.MapPost("/capacities", (RegisterCapacityRequest request, CapacityRegistry registry, ILogger<Program> logger) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var snapshot = registry.Register(request.Id, request.Region, request.TotalUnits);
    if (snapshot is null)
    {
        return Results.Problem(
            title: "Cannot shrink capacity below units in use",
            statusCode: StatusCodes.Status409Conflict);
    }

    logger.LogInformation(
        "Capacity {CapacityId} registered in {Region} with {TotalUnits} units",
        snapshot.Id, snapshot.Region, snapshot.TotalUnits);
    return Results.Created($"/capacities/{snapshot.Id}", snapshot);
});

app.MapGet("/capacities", (CapacityRegistry registry) => Results.Ok(registry.List()));

// Capacities call this every few seconds to prove they are alive.
app.MapPost("/capacities/{id}/heartbeat", (string id, CapacityRegistry registry, ILogger<Program> logger) =>
{
    if (!registry.Heartbeat(id, out var recovered))
    {
        return Results.NotFound();
    }

    if (recovered)
    {
        logger.LogInformation("Capacity {CapacityId} recovered and is healthy again", id);
    }
    return Results.NoContent();
});

// Route a workload to the best healthy capacity and reserve units on it.
app.MapPost("/route", (RouteRequest request, CapacityRegistry registry, RouterMetrics metrics, ILogger<Program> logger) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
    {
        metrics.RouteRequested("invalid");
        return Results.ValidationProblem(errors);
    }

    var result = registry.Route(request);
    if (result.Succeeded)
    {
        var allocation = result.Allocation!;
        metrics.RouteRequested("routed");
        logger.LogInformation(
            "Workload {WorkloadId} routed to {CapacityId} ({Region}) for {Units} units",
            allocation.WorkloadId, allocation.CapacityId, allocation.Region, allocation.Units);
        return Results.Ok(allocation);
    }

    var outcome = result.Failure == RouteFailure.NoHealthyCapacity ? "no_healthy_capacity" : "insufficient_capacity";
    metrics.RouteRequested(outcome);
    logger.LogWarning(
        "Could not route workload {WorkloadId} ({Units} units): {Outcome}",
        request.WorkloadId, request.Units, outcome);
    return Results.Problem(
        title: "No capacity available",
        detail: outcome,
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

// Release the units held by an allocation.
app.MapDelete("/allocations/{id}", (string id, CapacityRegistry registry) =>
    registry.Release(id) ? Results.NoContent() : Results.NotFound());

app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>.
public partial class Program { }
