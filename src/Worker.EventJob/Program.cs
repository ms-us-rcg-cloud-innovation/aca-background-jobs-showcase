using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Jobs.Shared;

// ---------------------------------------------------------------------------
// Event-driven Azure Container Apps Job.
//
// Hangfire equivalent: the worker that dequeues BackgroundJob.Enqueue items.
// Here KEDA scales this Job based on Service Bus queue depth. Each execution
// drains available messages, then exits (scale-to-zero when the queue is empty).
//
// Message-level reliability is provided by Service Bus:
//   - success  -> CompleteMessageAsync
//   - failure  -> AbandonMessageAsync (redelivered up to MaxDeliveryCount,
//                 then automatically dead-lettered) === Hangfire retries.
// ---------------------------------------------------------------------------

var fqns = Environment.GetEnvironmentVariable("SERVICEBUS_FQNS")
           ?? throw new InvalidOperationException("SERVICEBUS_FQNS is not set.");
var queueName = Environment.GetEnvironmentVariable("SERVICEBUS_QUEUE") ?? "jobs";
var maxMessages = int.TryParse(Environment.GetEnvironmentVariable("MAX_MESSAGES"), out var mm) ? mm : 20;

var repo = JobRepository.FromEnvironment();
await repo.EnsureSchemaAsync();
var processor = new JobProcessor(repo);

await using var client = new ServiceBusClient(fqns, new DefaultAzureCredential());
await using var receiver = client.CreateReceiver(queueName, new ServiceBusReceiverOptions
{
    ReceiveMode = ServiceBusReceiveMode.PeekLock
});

Console.WriteLine($"Event worker started. Draining up to {maxMessages} messages from '{queueName}'.");
var processed = 0;

while (processed < maxMessages)
{
    var msg = await receiver.ReceiveMessageAsync(maxWaitTime: TimeSpan.FromSeconds(5));
    if (msg is null)
    {
        Console.WriteLine("Queue empty. Exiting.");
        break;
    }

    JobMessage? job;
    try
    {
        job = JsonSerializer.Deserialize<JobMessage>(msg.Body.ToString());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Poison message {msg.MessageId}: {ex.Message}. Dead-lettering.");
        await receiver.DeadLetterMessageAsync(msg, "DeserializationError", ex.Message);
        continue;
    }

    if (job is null)
    {
        await receiver.DeadLetterMessageAsync(msg, "NullPayload", "Body did not deserialize to a JobMessage.");
        continue;
    }

    var record = new JobRecord
    {
        Id = job.JobId,
        JobType = job.JobType,
        TriggerKind = job.TriggerKind,
        Payload = job.Payload,
        CreatedUtc = DateTime.UtcNow
    };

    // Service Bus delivery count = attempt number (1-based).
    var ok = await processor.RunAsync(record, attempt: (int)msg.DeliveryCount, CancellationToken.None);
    if (ok)
        await receiver.CompleteMessageAsync(msg);
    else
        await receiver.AbandonMessageAsync(msg); // redelivered / eventually dead-lettered

    processed++;
}

Console.WriteLine($"Event worker finished. Processed {processed} message(s).");
return 0;
