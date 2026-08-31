using Jobs.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Ensure the Azure SQL schema exists on startup (idempotent).
try
{
    await JobRepository.FromEnvironment().EnsureSchemaAsync();
    app.Logger.LogInformation("Azure SQL schema verified.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not verify SQL schema on startup (will retry on first request).");
}

app.MapGet("/", () => Results.Ok(new
{
    service = "producer-api",
    description = "Azure-native replacement for the Hangfire client API. Enqueue and schedule background work onto Azure Service Bus; Azure Container Apps Jobs process it.",
    endpoints = new[]
    {
        "POST /jobs/enqueue   -> fire-and-forget (Hangfire BackgroundJob.Enqueue)",
        "POST /jobs/schedule  -> delayed        (Hangfire BackgroundJob.Schedule)",
        "GET  /jobs           -> recent executions",
        "GET  /jobs/stats     -> status counts",
        "GET  /health         -> liveness"
    }
}));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Fire-and-forget: enqueue work for immediate processing.
app.MapPost("/jobs/enqueue", async (EnqueueRequest req) =>
{
    var repo = JobRepository.FromEnvironment();
    await repo.EnsureSchemaAsync();
    await using var sender = QueueSender.FromEnvironment();

    var msg = new JobMessage
    {
        JobType = req.JobType ?? "sample",
        TriggerKind = TriggerKind.FireAndForget,
        Payload = req.Payload ?? "{}"
    };

    await repo.InsertAsync(new JobRecord
    {
        Id = msg.JobId,
        JobType = msg.JobType,
        TriggerKind = msg.TriggerKind,
        Status = JobStatus.Enqueued,
        Payload = msg.Payload
    });
    await sender.SendAsync(msg);

    return Results.Accepted($"/jobs/{msg.JobId}", new { jobId = msg.JobId, status = JobStatus.Enqueued });
});

// Delayed: schedule work to become visible after N seconds (Service Bus scheduled message).
app.MapPost("/jobs/schedule", async (ScheduleRequest req) =>
{
    var delay = TimeSpan.FromSeconds(Math.Max(0, req.DelaySeconds));
    var when = DateTimeOffset.UtcNow.Add(delay);

    var repo = JobRepository.FromEnvironment();
    await repo.EnsureSchemaAsync();
    await using var sender = QueueSender.FromEnvironment();

    var msg = new JobMessage
    {
        JobType = req.JobType ?? "sample",
        TriggerKind = TriggerKind.Delayed,
        Payload = req.Payload ?? "{}"
    };

    await repo.InsertAsync(new JobRecord
    {
        Id = msg.JobId,
        JobType = msg.JobType,
        TriggerKind = msg.TriggerKind,
        Status = JobStatus.Enqueued,
        Payload = msg.Payload
    });
    await sender.SendAsync(msg, when);

    return Results.Accepted($"/jobs/{msg.JobId}", new { jobId = msg.JobId, status = JobStatus.Enqueued, visibleAtUtc = when });
});

app.MapGet("/jobs", async (int? limit) =>
{
    var repo = JobRepository.FromEnvironment();
    var jobs = await repo.GetRecentAsync(limit ?? 50);
    return Results.Ok(jobs);
});

app.MapGet("/jobs/stats", async () =>
{
    var repo = JobRepository.FromEnvironment();
    var stats = await repo.GetStatusCountsAsync();
    return Results.Ok(stats);
});

app.Run();

record EnqueueRequest(string? JobType, string? Payload);
record ScheduleRequest(string? JobType, string? Payload, int DelaySeconds);
