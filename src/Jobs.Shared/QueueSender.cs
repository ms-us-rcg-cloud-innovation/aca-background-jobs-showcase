using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace Jobs.Shared;

/// <summary>
/// Publishes work onto an Azure Service Bus queue. This is the Azure-native
/// replacement for Hangfire's client API (BackgroundJob.Enqueue / .Schedule):
///   - SendAsync(msg)                -> fire-and-forget
///   - SendAsync(msg, scheduleUtc)   -> delayed (Service Bus scheduled message)
/// KEDA on Azure Container Apps then scales an event-driven Job based on the
/// queue depth to process these messages.
///
/// Authentication is passwordless via DefaultAzureCredential (managed identity
/// in Azure, developer credential locally). Set SERVICEBUS_FQNS + SERVICEBUS_QUEUE.
/// </summary>
public sealed class QueueSender : IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public QueueSender(string fullyQualifiedNamespace, string queueName)
    {
        _client = new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential());
        _sender = _client.CreateSender(queueName);
    }

    public static QueueSender FromEnvironment()
    {
        var fqns = Environment.GetEnvironmentVariable("SERVICEBUS_FQNS")
                   ?? throw new InvalidOperationException("SERVICEBUS_FQNS is not set.");
        var queue = Environment.GetEnvironmentVariable("SERVICEBUS_QUEUE") ?? "jobs";
        return new QueueSender(fqns, queue);
    }

    public async Task SendAsync(JobMessage message, DateTimeOffset? scheduleUtc = null, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(message);
        var sb = new ServiceBusMessage(body)
        {
            MessageId = message.JobId.ToString(),
            Subject = message.JobType,
            ContentType = "application/json",
        };
        sb.ApplicationProperties["triggerKind"] = message.TriggerKind;

        if (scheduleUtc is { } when && when > DateTimeOffset.UtcNow)
            await _sender.ScheduleMessageAsync(sb, when, ct);
        else
            await _sender.SendMessageAsync(sb, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
