# Architecture

## Components

| Component | Type | Role |
|---|---|---|
| `producer-api` | Container App (HTTP ingress) | Accepts enqueue/schedule requests, writes an `Enqueued` row to SQL, and sends/schedules a Service Bus message. Replaces the Hangfire *client* API. |
| `dashboard` | Container App (HTTP ingress) | Reads `dbo.JobExecutions` and renders a live, auto-refreshing view. Replaces the Hangfire *dashboard*. |
| `event-worker` | Container Apps **Job** (Event trigger) | KEDA scales it on Service Bus queue depth. Drains messages, runs each unit of work, completes or abandons (→ retry/dead-letter). Handles fire-and-forget + delayed. |
| `scheduled-worker` | Container Apps **Job** (Schedule trigger) | Runs on a CRON expression defined on the platform. Represents recurring maintenance work; can fan out child items to the queue. |
| `manual-worker` | Container Apps **Job** (Manual trigger) | Started on demand for long-running batch work with no fixed timeout. |
| Azure Service Bus | Queue `jobs` | Durable job queue. `maxDeliveryCount=5`, dead-lettering enabled. Also provides scheduled (delayed) messages. |
| Azure SQL | DB `jobs` | Single `dbo.JobExecutions` table tracks lifecycle (status, attempts, timing, worker replica, error). |
| User-assigned managed identity | Identity | Passwordless auth: **Service Bus Data Owner**, **AcrPull**, and a SQL DB user. Pinned via `AZURE_CLIENT_ID`. |
| Log Analytics | Workspace | Collects logs/console output from every app and job replica. |

## Request/execution flow

1. A caller `POST`s to `producer-api` (`/jobs/enqueue` or `/jobs/schedule`).
2. Producer inserts an `Enqueued` row into Azure SQL and sends a Service Bus
   message (immediately, or scheduled for the future).
3. **KEDA** observes queue depth and starts one or more `event-worker`
   executions (from zero).
4. A worker receives a message under **PeekLock**, sets the row to `Processing`,
   runs the work, then:
   - success → `Succeeded` + `CompleteMessage`
   - failure → `Failed` + `AbandonMessage` (redelivered; after 5 tries →
     dead-letter queue)
5. `scheduled-worker` runs independently on its CRON; `manual-worker` runs when
   started. Both write their lifecycle to the same table.
6. The `dashboard` app reads the table and shows status counts + recent
   executions, refreshing every few seconds.

## Data model — `dbo.JobExecutions`

| Column | Notes |
|---|---|
| `Id` | GUID (also the Service Bus `MessageId`) |
| `JobType` | Logical job name |
| `TriggerKind` | fire-and-forget / delayed / recurring / manual |
| `Status` | Enqueued → Processing → Succeeded / Failed |
| `Payload` | JSON input (`{"fail":true}` forces a failure for the demo) |
| `Attempt` | From Service Bus `DeliveryCount` for event jobs |
| `CreatedUtc` / `StartedUtc` / `CompletedUtc` | Timing |
| `WorkerReplica` | From `CONTAINER_APP_REPLICA_NAME` — shows which replica ran it |
| `Error` | Exception message on failure |

The table is created on first run by any component (`EnsureSchemaAsync`), which
is why the managed identity is granted `db_ddladmin` in addition to read/write.

## Scaling behavior

- **event-worker:** `minExecutions: 0`, `maxExecutions: 10`, KEDA
  `azure-servicebus` rule with `messageCount: 5` (roughly one execution per 5
  queued messages, up to the max). Scales to zero when the queue is empty.
- **producer-api / dashboard:** HTTP scale rule (`concurrentRequests: 50`),
  `minReplicas: 1` so the UI/API is always warm.
- **scheduled-worker / manual-worker:** one replica per run (`parallelism: 1`).

## Security

- No secrets in code or config for data-plane access. `DefaultAzureCredential`
  and SqlClient `Authentication=Active Directory Default` both resolve to the
  user-assigned managed identity (pinned by `AZURE_CLIENT_ID`).
- SQL uses an Entra ID admin (your signed-in user at deploy time) plus a
  contained DB user for the managed identity. The SQL admin password parameter
  exists only for server creation and initial setup.
- The demo opens the SQL firewall to Azure services for simplicity. For real
  use, prefer **private endpoints** / VNet integration and remove the
  `0.0.0.0` rule.

## Extending the demo

- Add **fan-in** aggregation to complete a batch when all children finish.
- Swap the simulated workload in `JobProcessor.RunAsync` for a real task
  (e.g., calling the fulfiller API and writing results to SQL).
- Add **Durable Functions** alongside to contrast stateful orchestration with
  the queue-driven fan-out shown here.
- Front the producer with **APIM** or add auth to the ingress.
