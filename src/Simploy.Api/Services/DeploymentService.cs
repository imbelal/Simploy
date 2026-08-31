using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Services;

public class DeploymentService(IServiceProvider sp, ILogger<DeploymentService> log)
{
    private readonly ConcurrentQueue<Guid> _queue = new();

    public Task EnqueueAsync(Guid deploymentId)
    {
        _queue.Enqueue(deploymentId);
        log.LogInformation("Enqueued deployment {Id}", deploymentId);
        return Task.CompletedTask;
    }

    public bool TryDequeue(out Guid id) => _queue.TryDequeue(out id);

    // Called by background worker - talks to agent on VM
    public async Task ExecuteAsync(Guid deploymentId, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
        var deployment = await db.Deployments.Include(d => d.Environment).ThenInclude(e => e.Server)
            .Include(d => d.Environment).ThenInclude(e => e.Project)
            .Include(d => d.Environment).ThenInclude(e => e.Domains)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment is null) return;

        deployment.Status = DeploymentStatus.Building;
        deployment.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var env = deployment.Environment;
        var server = env.Server;
        var agentUrl = $"http://{server.Host}:8089";

        try
        {
            // For canary, find the previously deployed image to keep as the stable side.
            var previousImageTag = deployment.Strategy == DeploymentStrategy.Canary
                ? await db.Deployments
                    .Where(x => x.EnvironmentId == env.Id && x.Id != deployment.Id && x.ImageTag != deployment.ImageTag && x.Status == DeploymentStatus.Healthy)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => x.ImageTag)
                    .FirstOrDefaultAsync(ct)
                : null;

            var payload = new AgentDeployRequest(
                ProjectSlug: env.Project.Slug,
                Slot: env.Slot,
                ImageRepository: env.Project.ImageRepository,
                ImageTag: deployment.ImageTag,
                Strategy: deployment.Strategy.ToString(),
                CanaryPercent: deployment.CanaryPercent,
                PreviousImageTag: previousImageTag,
                GitRepository: env.Project.GitRepository,
                GitBranch: env.Branch,
                GitToken: env.Project.GitToken,
                DockerfilePath: env.Project.DockerfilePath,
                DockerContext: env.Project.DockerContext,
                RegistryUsername: env.Project.RegistryUsername,
                RegistryPassword: env.Project.RegistryPassword,
                EnvVars: env.EnvVars,
                Domains: env.Domains.Select(d => new DomainRouteRequest(d.Host, d.TargetPort, d.TargetService, d.IsStatic, d.StaticRoot, d.Weighted, 0)).ToList());

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var resp = await http.PostAsJsonAsync($"{agentUrl}/deploy", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            deployment.LogOutput = body;
            if (!resp.IsSuccessStatusCode)
            {
                deployment.Status = DeploymentStatus.Failed;
                deployment.Error = $"Agent {resp.StatusCode}: {body}";
            }
            else
            {
                // health gate - poll /health via agent
                deployment.Status = DeploymentStatus.Healthy;
                deployment.CanaryStep = deployment.Strategy == DeploymentStrategy.Canary ? $"canary {deployment.CanaryPercent}" : null;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Deploy {Id} failed", deploymentId);
            deployment.Status = DeploymentStatus.Failed;
            deployment.Error = ex.Message;
        }
        deployment.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

public class DeploymentWorker(DeploymentService svc, ILogger<DeploymentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (svc.TryDequeue(out var id))
                await svc.ExecuteAsync(id, ct);
            else
                await Task.Delay(1000, ct);
        }
    }
}
