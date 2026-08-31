using System.Text.Json;
using Jobs.Shared;

// ---------------------------------------------------------------------------
// Scheduled (CRON) Azure Container Apps Job.
//
// Hangfire equivalent: RecurringJob.AddOrUpdate(..., Cron.Daily).
// The CRON schedule is declared on the ACA Job itself (see infra/main.bicep),
// so there is no always-on process polling a database like Hangfire's server.
// ACA starts a container on schedule, it runs to completion, then scales to zero.
//
// This sample represents a recurring maintenance task (e.g. nightly
// reconciliation). It records its own execution and can optionally fan out
// additional fire-and-forget work onto the queue.
// ---------------------------------------------------------------------------

var repo = JobRepository.FromEnvironment();
await repo.EnsureSchemaAsync();
var processor = new JobProcessor(repo);

var record = new JobRecord
{
    Id = Guid.NewGuid(),
    JobType = "nightly-reconciliation",
    TriggerKind = TriggerKind.Recurring,
    Status = JobStatus.Enqueued,
    Payload = JsonSerializer.Serialize(new { scheduledAtUtc = DateTime.UtcNow, source = "cron" }),
    CreatedUtc = DateTime.UtcNow
};
await repo.InsertAsync(record);

var ok = await processor.RunAsync(record, attempt: 1);

// Optional fan-out: enqueue follow-up work discovered by the recurring task.
var fanOut = int.TryParse(Environment.GetEnvironmentVariable("FANOUT_COUNT"), out var f) ? f : 0;
if (fanOut > 0)
{
    await using var sender = QueueSender.FromEnvironment();
    for (var i = 0; i < fanOut; i++)
    {
        var child = new JobMessage
        {
            JobType = "reconciliation-item",
            TriggerKind = TriggerKind.FireAndForget,
            Payload = JsonSerializer.Serialize(new { index = i, parent = record.Id })
        };
        await repo.InsertAsync(new JobRecord
        {
            Id = child.JobId,
            JobType = child.JobType,
            TriggerKind = child.TriggerKind,
            Status = JobStatus.Enqueued,
            Payload = child.Payload
        });
        await sender.SendAsync(child);
    }
    Console.WriteLine($"Recurring job fanned out {fanOut} follow-up item(s) to the queue.");
}

Console.WriteLine($"Scheduled job complete. Success={ok}.");
return ok ? 0 : 1;
