namespace Jobs.Shared;

/// <summary>
/// How a unit of work was triggered. These map directly onto Hangfire concepts:
///   FireAndForget -> BackgroundJob.Enqueue
///   Delayed       -> BackgroundJob.Schedule
///   Recurring     -> RecurringJob.AddOrUpdate (CRON)
///   Manual        -> ad-hoc trigger / Hangfire "Trigger now"
/// In this showcase every kind is delivered by an Azure-native primitive
/// (Service Bus + Azure Container Apps Jobs) instead of Hangfire.
/// </summary>
public static class TriggerKind
{
    public const string FireAndForget = "fire-and-forget";
    public const string Delayed = "delayed";
    public const string Recurring = "recurring";
    public const string Manual = "manual";
    public const string Event = "event";
}

public static class JobStatus
{
    public const string Enqueued = "Enqueued";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>Message contract placed on the Service Bus queue.</summary>
public sealed class JobMessage
{
    public Guid JobId { get; set; } = Guid.NewGuid();
    public string JobType { get; set; } = "sample";
    public string TriggerKind { get; set; } = Jobs.Shared.TriggerKind.FireAndForget;
    public string Payload { get; set; } = "{}";
}

/// <summary>Row persisted to Azure SQL to track a job's lifecycle.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = "sample";
    public string TriggerKind { get; set; } = Jobs.Shared.TriggerKind.FireAndForget;
    public string Status { get; set; } = JobStatus.Enqueued;
    public string Payload { get; set; } = "{}";
    public int Attempt { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? WorkerReplica { get; set; }
    public string? Error { get; set; }
}

public sealed class JobStatusCount
{
    public string Status { get; set; } = "";
    public int Count { get; set; }
}
