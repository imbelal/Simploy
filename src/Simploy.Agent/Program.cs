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

app.MapPost("/deploy", async (AgentDeployRequest req, DockerService docker, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("Deploy {Project}/{Slot} image={Image} strategy={Strategy}", req.ProjectSlug, req.Slot, $"{req.ImageRepository}:{req.ImageTag}", req.Strategy);
    try
    {
        var output = await docker.DeployAsync(req, ct);
        return Results.Ok(new { ok = true, output });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Deploy failed");
        return Results.Problem($"{ex.Message}");
    }
});

app.MapGet("/containers", async (DockerService docker) => Results.Ok(await docker.ListContainersAsync()));

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
