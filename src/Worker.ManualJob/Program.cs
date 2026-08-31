using System.Text.Json;
using Jobs.Shared;

// ---------------------------------------------------------------------------
// Manual (on-demand) Azure Container Apps Job.
//
// Hangfire equivalent: hitting "Trigger now" in the dashboard, or an ad-hoc
// BackgroundJob.Enqueue for a heavy batch. Start this Job on demand with:
//   az containerapp job start -n <manual-job> -g <rg>
// or from the Producer API / a button in the dashboard.
//
// Demonstrates a long-running batch that would exceed the Azure Functions
// consumption timeout but runs comfortably as an ACA Job (no fixed timeout).
// BATCH_SIZE controls how many items are processed.
// ---------------------------------------------------------------------------

var batchSize = int.TryParse(Environment.GetEnvironmentVariable("BATCH_SIZE"), out var b) ? b : 5;

var repo = JobRepository.FromEnvironment();
await repo.EnsureSchemaAsync();
var processor = new JobProcessor(repo);

Console.WriteLine($"Manual batch job started. Processing {batchSize} item(s).");
var succeeded = 0;

for (var i = 0; i < batchSize; i++)
{
    var record = new JobRecord
    {
        Id = Guid.NewGuid(),
        JobType = "manual-batch-item",
        TriggerKind = TriggerKind.Manual,
        Status = JobStatus.Enqueued,
        Payload = JsonSerializer.Serialize(new { index = i, batchSize }),
        CreatedUtc = DateTime.UtcNow
    };
    await repo.InsertAsync(record);
    if (await processor.RunAsync(record, attempt: 1))
        succeeded++;
}

Console.WriteLine($"Manual batch job complete. {succeeded}/{batchSize} succeeded.");
return succeeded == batchSize ? 0 : 1;
