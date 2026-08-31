using Microsoft.Data.SqlClient;

namespace Jobs.Shared;

/// <summary>
/// Minimal Azure SQL persistence for job state. Replaces Hangfire's SQL Server
/// storage schema with a single explicit table so the audience can see exactly
/// what is being stored. Matches the customer's "MS SQL only" constraint.
///
/// Connection string is read from SQL_CONNECTION_STRING. Use either SQL auth or
/// passwordless AAD auth, e.g.:
///   Server=tcp:...database.windows.net,1433;Database=jobs;Authentication=Active Directory Default;Encrypt=True;
/// </summary>
public sealed class JobRepository
{
    private readonly string _connectionString;

    public JobRepository(string connectionString) => _connectionString = connectionString;

    public static JobRepository FromEnvironment()
    {
        var cs = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
                 ?? throw new InvalidOperationException("SQL_CONNECTION_STRING is not set.");
        return new JobRepository(cs);
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string sql = @"
IF OBJECT_ID('dbo.JobExecutions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.JobExecutions
    (
        Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        JobType       NVARCHAR(100)    NOT NULL,
        TriggerKind   NVARCHAR(50)     NOT NULL,
        Status        NVARCHAR(50)     NOT NULL,
        Payload       NVARCHAR(MAX)    NOT NULL,
        Attempt       INT              NOT NULL DEFAULT 0,
        CreatedUtc    DATETIME2        NOT NULL,
        StartedUtc    DATETIME2        NULL,
        CompletedUtc  DATETIME2        NULL,
        WorkerReplica NVARCHAR(200)    NULL,
        Error         NVARCHAR(MAX)    NULL
    );
    CREATE INDEX IX_JobExecutions_CreatedUtc ON dbo.JobExecutions (CreatedUtc DESC);
END";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAsync(JobRecord r, CancellationToken ct = default)
    {
        const string sql = @"
INSERT INTO dbo.JobExecutions
    (Id, JobType, TriggerKind, Status, Payload, Attempt, CreatedUtc, StartedUtc, CompletedUtc, WorkerReplica, Error)
VALUES
    (@Id, @JobType, @TriggerKind, @Status, @Payload, @Attempt, @CreatedUtc, @StartedUtc, @CompletedUtc, @WorkerReplica, @Error);";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(JobRecord r, CancellationToken ct = default)
    {
        const string sql = @"
UPDATE dbo.JobExecutions
   SET Status = @Status,
       Attempt = @Attempt,
       StartedUtc = @StartedUtc,
       CompletedUtc = @CompletedUtc,
       WorkerReplica = @WorkerReplica,
       Error = @Error
 WHERE Id = @Id;";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<JobRecord>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var sql = $@"
SELECT TOP (@Limit) Id, JobType, TriggerKind, Status, Payload, Attempt, CreatedUtc, StartedUtc, CompletedUtc, WorkerReplica, Error
FROM dbo.JobExecutions
ORDER BY CreatedUtc DESC;";
        var list = new List<JobRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new JobRecord
            {
                Id = reader.GetGuid(0),
                JobType = reader.GetString(1),
                TriggerKind = reader.GetString(2),
                Status = reader.GetString(3),
                Payload = reader.GetString(4),
                Attempt = reader.GetInt32(5),
                CreatedUtc = reader.GetDateTime(6),
                StartedUtc = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                CompletedUtc = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                WorkerReplica = reader.IsDBNull(9) ? null : reader.GetString(9),
                Error = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }
        return list;
    }

    public async Task<List<JobStatusCount>> GetStatusCountsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Status, COUNT(*) FROM dbo.JobExecutions GROUP BY Status;";
        var list = new List<JobStatusCount>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new JobStatusCount { Status = reader.GetString(0), Count = reader.GetInt32(1) });
        return list;
    }

    private static void Bind(SqlCommand cmd, JobRecord r)
    {
        cmd.Parameters.AddWithValue("@Id", r.Id);
        cmd.Parameters.AddWithValue("@JobType", r.JobType);
        cmd.Parameters.AddWithValue("@TriggerKind", r.TriggerKind);
        cmd.Parameters.AddWithValue("@Status", r.Status);
        cmd.Parameters.AddWithValue("@Payload", r.Payload);
        cmd.Parameters.AddWithValue("@Attempt", r.Attempt);
        cmd.Parameters.AddWithValue("@CreatedUtc", r.CreatedUtc);
        cmd.Parameters.AddWithValue("@StartedUtc", (object?)r.StartedUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedUtc", (object?)r.CompletedUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@WorkerReplica", (object?)r.WorkerReplica ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Error", (object?)r.Error ?? DBNull.Value);
    }
}
