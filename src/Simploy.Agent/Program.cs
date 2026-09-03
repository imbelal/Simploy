using Simploy.Agent;
using Simploy.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8089");
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<DockerService>();

var app = builder.Build();

// Agents are called by the control plane. Require a shared bearer token
// (SIMPLOY_AGENT_TOKEN) on every endpoint except /health so only Simploy can
// trigger deploys on this VM.
var agentToken = builder.Configuration["Agent:Token"] ?? "";
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path == "/health";
    var auth = ctx.Request.Headers.Authorization.ToString();
    var ok = path || (!string.IsNullOrEmpty(agentToken) && auth.Equals($"Bearer {agentToken}", StringComparison.Ordinal));
    if (!ok)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }
    await next();
});

// Agent API - called by Simploy.Api control plane. /health is public
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0", ts = DateTime.UtcNow }));

app.MapPost("/deploy", (AgentDeployRequest req, DockerService docker, ILogger<Program> log) =>
{
    var job = JobStore.Create();
    log.LogInformation("Deploy {Project}/{Slot} image={Image} strategy={Strategy} (job {Id})", req.ProjectSlug, req.Slot, $"{req.ImageRepository}:{req.ImageTag}", req.Strategy, job.Id);

    _ = Task.Run(async () =>
    {
        DeployLog.CurrentJob = job;
        try
        {
            var output = await docker.DeployAsync(req, CancellationToken.None);
            DeployLog.Write(output);
            job.Status = "Success"; job.Done = true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Deploy failed (job {Id})", job.Id);
            job.Status = "Failed"; job.Error = ex.Message; job.Done = true;
        }
        finally { DeployLog.CurrentJob = null; job.FinishedAt = DateTime.UtcNow; }
    });

    return Results.Ok(new { jobId = job.Id });
});

app.MapGet("/deploy/{jobId}", (string jobId) =>
{
    var job = JobStore.Get(jobId);
    if (job is null) return Results.NotFound(new { error = "job not found" });
    return Results.Ok(new { jobId = job.Id, status = job.Status, done = job.Done, error = job.Error,
        logs = job.Logs.ToString(), started = job.StartedAt, finished = job.FinishedAt });
});

app.MapGet("/containers", async (DockerService docker) =>
    Results.Ok((await docker.ListContainersAsync()).Select(c => new
    {
        name = c.Names.FirstOrDefault()?.TrimStart('/'),
        image = c.Image,
        state = c.State,
        status = c.Status,
        project = c.Labels.TryGetValue("com.docker.compose.project", out var pj) ? pj : null,
        service = c.Labels.TryGetValue("com.docker.compose.service", out var sv) ? sv : null,
    })));

app.MapGet("/containers/{name}/logs", async (string name, string? tail, HttpContext ctx, DockerService docker, CancellationToken ct) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.Body.FlushAsync(ct);
    int t = int.TryParse(tail, out var ti) ? ti : 200;
    await docker.StreamContainerLogsAsync(name, t, async line =>
    {
        await ctx.Response.WriteAsync($"data: {line}\n\n");
        await ctx.Response.Body.FlushAsync(ct);
    }, ct);
});

app.MapGet("/certificates", async (DockerService docker, CancellationToken ct) =>
    Results.Ok(await docker.ListCertificatesAsync(ct)));

app.MapGet("/metrics/containers", async (DockerService docker, CancellationToken ct) =>
    Results.Ok(await docker.ListContainerMetricsAsync(ct)));

app.MapPost("/system/containers/restart", async (DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    var names = await docker.RestartAppContainersAsync(ct);
    log.LogInformation("Restarted app containers: {Names}", string.Join(", ", names));
    return Results.Ok(new { restarted = names.Count });
});

// Per-container management (start/stop/restart/delete a single container). Skips
// the control-plane containers (name starts with `simploy-`) so a misclick can't
// take down the API/agent/postgres/caddy.
app.MapPost("/containers/{name}/stop", async (string name, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    if (name.StartsWith("simploy-", StringComparison.Ordinal))
        return Results.Problem("Refusing to stop a control-plane container", statusCode: 400);
    log.LogInformation("Stop container {Name}", name);
    try { return Results.Ok(new { ok = true, output = await docker.ContainerActionAsync(name, "stop", ct) }); }
    catch (Exception ex) { log.LogError(ex, "stop {Name} failed", name); return Results.Problem(ex.Message); }
});

app.MapPost("/containers/{name}/start", async (string name, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("Start container {Name}", name);
    try { return Results.Ok(new { ok = true, output = await docker.ContainerActionAsync(name, "start", ct) }); }
    catch (Exception ex) { log.LogError(ex, "start {Name} failed", name); return Results.Problem(ex.Message); }
});

app.MapPost("/containers/{name}/restart", async (string name, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    if (name.StartsWith("simploy-", StringComparison.Ordinal))
        return Results.Problem("Refusing to restart a control-plane container", statusCode: 400);
    log.LogInformation("Restart container {Name}", name);
    try { return Results.Ok(new { ok = true, output = await docker.ContainerActionAsync(name, "restart", ct) }); }
    catch (Exception ex) { log.LogError(ex, "restart {Name} failed", name); return Results.Problem(ex.Message); }
});

// `delete` forces the container down + removes it. The compose project can
// recreate it on the next deploy, but data in named volumes is preserved.
app.MapPost("/containers/{name}/delete", async (string name, bool force, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    if (name.StartsWith("simploy-", StringComparison.Ordinal))
        return Results.Problem("Refusing to delete a control-plane container", statusCode: 400);
    log.LogInformation("Delete container {Name} (force={Force})", name, force);
    try { return Results.Ok(new { ok = true, output = await docker.DeleteContainerAsync(name, force, ct) }); }
    catch (Exception ex) { log.LogError(ex, "delete {Name} failed", name); return Results.Problem(ex.Message); }
});

// Managed database provisioning
app.MapPost("/db/deploy", async (AgentDbRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("Provision database {Name} ({Type}:{Version})", req.DbName, req.Type, req.Version);
    try { return Results.Ok(new { ok = true, output = await docker.ProvisionDatabaseAsync(req, ct) }); }
    catch (Exception ex) { log.LogError(ex, "db deploy failed"); return Results.Problem(ex.Message); }
});

app.MapPost("/db/remove", async (AgentDbRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("Remove database {Name}", req.DbName);
    try { return Results.Ok(new { ok = true, output = await docker.RemoveDatabaseAsync(req, ct) }); }
    catch (Exception ex) { log.LogError(ex, "db remove failed"); return Results.Problem(ex.Message); }
});

// Control-plane auto-repair: reset the postgres superuser password in-place so the API
// can connect again (no data loss). Called by Simploy.Api on startup on a 28P01 auth fail.
app.MapPost("/system/db/fix-password", async (AgentFixPasswordRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("Fix postgres password for role {Role}", req.Role);
    try { return Results.Ok(new { ok = true, output = await docker.FixPostgresPasswordAsync(req, ct) }); }
    catch (Exception ex) { log.LogError(ex, "fix db password failed"); return Results.Problem(ex.Message); }
});

// Simploy control-plane backup (dump the Postgres database via docker exec)
app.MapPost("/system/backup", async (AgentBackupRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    try { return Results.Ok(new { ok = true, output = await docker.RunBackupAsync(req, ct) }); }
    catch (Exception ex) { log.LogError(ex, "backup failed"); return Results.Problem(ex.Message); }
});

app.MapGet("/system/backup/list", async (string dir, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    try { return Results.Ok(docker.ListBackups(dir)); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/system/backup/restore", async (AgentBackupRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    try { return Results.Ok(new { ok = true, output = await docker.RestoreBackupAsync(req, ct) }); }
    catch (Exception ex) { log.LogError(ex, "restore failed"); return Results.Problem(ex.Message); }
});

app.Run();
