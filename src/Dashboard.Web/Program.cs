using Jobs.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/jobs", async (int? limit) =>
{
    var repo = JobRepository.FromEnvironment();
    await repo.EnsureSchemaAsync();
    return Results.Ok(await repo.GetRecentAsync(limit ?? 100));
});

app.MapGet("/api/stats", async () =>
{
    var repo = JobRepository.FromEnvironment();
    await repo.EnsureSchemaAsync();
    return Results.Ok(await repo.GetStatusCountsAsync());
});

app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html"));

app.Run();

static class DashboardPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>ACA Jobs Dashboard</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: Segoe UI, system-ui, sans-serif; margin: 0; background:#0f1115; color:#e7e9ee; }
  header { padding: 18px 24px; background:#12141a; border-bottom:1px solid #262a33; }
  h1 { font-size: 18px; margin:0; }
  .sub { color:#8b909c; font-size:13px; margin-top:4px; }
  .wrap { padding: 20px 24px; }
  .cards { display:flex; gap:12px; flex-wrap:wrap; margin-bottom:18px; }
  .card { background:#171a21; border:1px solid #262a33; border-radius:10px; padding:14px 18px; min-width:120px; }
  .card .n { font-size:26px; font-weight:600; }
  .card .l { color:#8b909c; font-size:12px; text-transform:uppercase; letter-spacing:.04em; }
  table { width:100%; border-collapse: collapse; font-size:13px; }
  th, td { text-align:left; padding:8px 10px; border-bottom:1px solid #22262f; white-space:nowrap; }
  th { color:#8b909c; font-weight:600; position:sticky; top:0; background:#0f1115; }
  .badge { padding:2px 8px; border-radius:20px; font-size:12px; font-weight:600; }
  .Succeeded { background:#12351f; color:#4ade80; }
  .Failed { background:#3a1620; color:#f87171; }
  .Processing { background:#1d2b45; color:#60a5fa; }
  .Enqueued { background:#2b2718; color:#fbbf24; }
  .kind { color:#a5b0c2; }
  code { color:#c8ccd6; }
  .foot { color:#6b7180; font-size:12px; margin-top:14px; }
</style>
</head>
<body>
<header>
  <h1>Azure Container Apps &mdash; Background Jobs Dashboard</h1>
  <div class="sub">Azure-native replacement for the Hangfire dashboard. Auto-refreshes every 3s from Azure SQL.</div>
</header>
<div class="wrap">
  <div class="cards" id="cards"></div>
  <table>
    <thead><tr>
      <th>Created (UTC)</th><th>Job Type</th><th>Trigger</th><th>Status</th>
      <th>Attempt</th><th>Replica</th><th>Duration</th><th>Error</th>
    </tr></thead>
    <tbody id="rows"></tbody>
  </table>
  <div class="foot" id="foot"></div>
</div>
<script>
function dur(a,b){ if(!a||!b) return ''; const ms=new Date(b)-new Date(a); return (ms/1000).toFixed(1)+'s'; }
function fmt(d){ return d ? new Date(d).toISOString().replace('T',' ').replace('Z','') : ''; }
async function refresh(){
  try {
    const [jobs, stats] = await Promise.all([
      fetch('/api/jobs?limit=100').then(r=>r.json()),
      fetch('/api/stats').then(r=>r.json())
    ]);
    const order=['Enqueued','Processing','Succeeded','Failed'];
    const map=Object.fromEntries(stats.map(s=>[s.status,s.count]));
    document.getElementById('cards').innerHTML = order.map(s=>
      `<div class="card"><div class="n">${map[s]||0}</div><div class="l">${s}</div></div>`).join('');
    document.getElementById('rows').innerHTML = jobs.map(j=>
      `<tr>
        <td><code>${fmt(j.createdUtc)}</code></td>
        <td>${j.jobType}</td>
        <td class="kind">${j.triggerKind}</td>
        <td><span class="badge ${j.status}">${j.status}</span></td>
        <td>${j.attempt}</td>
        <td class="kind">${j.workerReplica||''}</td>
        <td>${dur(j.startedUtc,j.completedUtc)}</td>
        <td style="color:#f87171">${j.error||''}</td>
      </tr>`).join('');
    document.getElementById('foot').textContent = 'Last updated ' + new Date().toLocaleTimeString() + ' — ' + jobs.length + ' recent executions';
  } catch(e){ document.getElementById('foot').textContent = 'Error: ' + e.message; }
}
refresh(); setInterval(refresh, 3000);
</script>
</body>
</html>
""";
}
