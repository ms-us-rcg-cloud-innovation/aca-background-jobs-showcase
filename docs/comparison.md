# Hangfire → Azure-native: feature-by-feature comparison

This document maps the Hangfire capabilities a .NET team typically relies on to
their Azure-native equivalents, and explains **why Azure Container Apps (ACA)
Jobs** is the primary showcase here — while being honest about when a different
service is the better fit.

## The three main replacement paths

| | **Azure Container Apps Jobs** (this repo) | **Azure Functions** (Timer/Queue/Durable) | **App Service WebJobs** |
|---|---|---|---|
| Best for | Containerized/microservice workloads; heavy or long-running jobs isolated from the web tier | Event-driven, short/bursty work; complex stateful workflows (Durable) | Simple background scripts colocated with an existing App Service monolith |
| Trigger model | Event (KEDA), Schedule (CRON), Manual | Timer, Queue, Blob, Service Bus, Event Hub, HTTP, Durable orchestrations | Continuous (queue poll) or scheduled (CRON) |
| Scaling | KEDA event-driven, **scale to zero** | Consumption auto-scale, scale to zero | Scales with the App Service plan; needs **Always On** |
| Max duration | No fixed timeout (great for long batches) | ~10 min on Consumption (longer on Premium/Durable) | Bound to the plan |
| Packaging | Any container image | Functions host + code | App Service deployment |
| Closest to Hangfire | ✅ Very close (dedicated worker per job) | Close for enqueue/CRON; Durable for batches | Close for colocated simple jobs |

> Rule of thumb: **already containerizing or want isolation → ACA Jobs.**
> **Short, bursty, or complex workflows → Functions/Durable.**
> **Small scripts next to an existing App Service app → WebJobs.**

## Hangfire concept → what this repo does

### Fire-and-forget — `BackgroundJob.Enqueue`
- **Hangfire:** serializes a method call into SQL; an in-process server polls and runs it.
- **Here:** `Producer.Api` sends a message to **Azure Service Bus**; an
  **event-driven ACA Job** is scaled up by **KEDA** based on queue depth,
  drains messages, then scales back to zero.
- **Why better:** no always-on polling process; compute cost only while work exists.

### Delayed — `BackgroundJob.Schedule`
- **Hangfire:** stores a scheduled job; the server promotes it when due.
- **Here:** a **Service Bus scheduled message** becomes visible at the target
  time and triggers the same event worker. Scheduling is handled by the broker.

### Recurring — `RecurringJob.AddOrUpdate(..., Cron.Daily)`
- **Hangfire:** a recurring entry the server checks against a CRON.
- **Here:** a **Scheduled ACA Job** with `cronExpression` in `main.bicep`. The
  schedule is **platform configuration**, not application code, and there is no
  server to keep running.

### Ad-hoc / "Trigger now"
- **Hangfire:** dashboard button or a manual enqueue.
- **Here:** a **Manual ACA Job** started with
  `az containerapp job start` (or from an API/pipeline). Ideal for long-running
  batches that would exceed a Functions consumption timeout.

### Automatic retries
- **Hangfire:** configurable retry attempts with backoff.
- **Here:** Service Bus **PeekLock** + `maxDeliveryCount` (5). A failed message
  is abandoned and redelivered; after the cap it is **dead-lettered** for
  inspection/replay. The event worker treats `DeliveryCount` as the attempt number.

### Batches & continuations (Hangfire Pro)
- **Hangfire Pro:** `BatchJob.StartNew` / `ContinueWith`.
- **Here:** the recurring worker **fans out** child items onto the queue
  (`FANOUT_COUNT`), each processed independently by the event worker — a
  queue-driven fan-out/fan-in pattern. For richer stateful orchestration
  (chaining, fan-in aggregation, human-in-the-loop), **Azure Durable Functions**
  is the stronger fit and is called out as the recommended option for that case.

### Dashboard
- **Hangfire:** built-in Dashboard UI backed by its SQL schema.
- **Here:** a small **Dashboard Container App** that reads `dbo.JobExecutions`
  from Azure SQL and auto-refreshes. Demonstrates that job state is in a plain,
  queryable table you own.

### Storage
- **Hangfire:** its own SQL Server schema (jobs, servers, locks, sets, hashes…).
- **Here:** a single explicit `dbo.JobExecutions` table in **Azure SQL** — the
  customer already standardizes on MS SQL, so state stays familiar and queryable.

## Security & operations advantages

| Concern | Hangfire on VMs today | This showcase |
|---|---|---|
| Secrets | SQL connection strings in config | **Managed identity** (passwordless) to SQL and Service Bus |
| Patching | You patch 2 VMs / IIS | Serverless containers; platform-managed hosts |
| Idle cost | Servers run 24/7 to poll | **Scale to zero** between jobs |
| Isolation | Jobs share the app process/VM | Each job type is an isolated container |
| Deploys | In-place on VMs | ACA **revisions** (blue-green) |
| Observability | Hangfire dashboard | Dashboard app + **Log Analytics** for all replicas |

## Discovery questions before choosing a target

Because "Hangfire does a lot," confirm which features are actually in use:
1. Which job types exist — fire-and-forget, recurring, delayed, batches, continuations?
2. Typical and worst-case **job duration**? (drives ACA Jobs vs Functions Consumption)
3. Throughput / concurrency needs and ordering requirements?
4. Are Hangfire **Pro** features (batches, continuations) used?
5. Any dependence on the Hangfire dashboard for ops (retry, requeue, delete)?
6. Data stores beyond MS SQL? Networking/private-endpoint requirements?

The answers determine whether **ACA Jobs**, **Functions/Durable**, **WebJobs**,
or a mix is the best landing zone.
