using System.Diagnostics;

namespace Jobs.Shared;

/// <summary>
/// Simulated unit of work + lifecycle persistence, shared by every worker.
/// Demonstrates the behaviours Hangfire gives you (attempt tracking, status
/// transitions, failure capture) implemented on plain Azure primitives.
/// </summary>
public sealed class JobProcessor
{
    private readonly JobRepository _repo;
    private readonly string _workerReplica;

    public JobProcessor(JobRepository repo)
    {
        _repo = repo;
        // CONTAINER_APP_REPLICA_NAME is injected by Azure Container Apps at runtime.
        _workerReplica = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
                         ?? Environment.MachineName;
    }

    /// <summary>
    /// Runs a job to completion, updating Azure SQL as it transitions
    /// Processing -> Succeeded/Failed. Returns true on success.
    /// A payload containing "fail":true forces a failure to demonstrate
    /// retry + dead-lettering.
    /// </summary>
    public async Task<bool> RunAsync(JobRecord record, int attempt, CancellationToken ct = default)
    {
        record.Attempt = attempt;
        record.Status = JobStatus.Processing;
        record.StartedUtc = DateTime.UtcNow;
        record.WorkerReplica = _workerReplica;
        record.Error = null;
        await _repo.UpdateAsync(record, ct);

        try
        {
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"[{_workerReplica}] processing {record.JobType} ({record.TriggerKind}) attempt {attempt} id={record.Id}");

            // Simulated workload. Replace with the real background task.
            var forceFail = record.Payload.Contains("\"fail\":true", StringComparison.OrdinalIgnoreCase);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (forceFail)
                throw new InvalidOperationException("Simulated failure (payload requested fail=true).");

            record.Status = JobStatus.Succeeded;
            record.CompletedUtc = DateTime.UtcNow;
            await _repo.UpdateAsync(record, ct);
            Console.WriteLine($"[{_workerReplica}] succeeded id={record.Id} in {sw.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception ex)
        {
            record.Status = JobStatus.Failed;
            record.CompletedUtc = DateTime.UtcNow;
            record.Error = ex.Message;
            await _repo.UpdateAsync(record, ct);
            Console.WriteLine($"[{_workerReplica}] FAILED id={record.Id}: {ex.Message}");
            return false;
        }
    }
}
