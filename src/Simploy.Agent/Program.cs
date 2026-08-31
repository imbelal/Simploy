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

app.Run();
