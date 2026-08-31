using Simploy.Agent;
using Simploy.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8089");
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<DockerService>();

var app = builder.Build();

// Agent API - called by Simploy.Api control plane
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
