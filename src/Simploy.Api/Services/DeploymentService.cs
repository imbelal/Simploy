using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Services;

public class DeploymentService(IServiceProvider sp, IConfiguration config, GitHubAppService github, ILogger<DeploymentService> log)
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

            // If the project uses a GitHub App installation (and no PAT), mint a short-lived
            // installation token to clone the repo with. Falls back to the PAT if set.
            var gitToken = env.Project.GitToken;
            if (string.IsNullOrEmpty(gitToken) && !string.IsNullOrEmpty(env.Project.GithubInstallationId)
                && github.IsConfigured)
            {
                try { gitToken = await github.GetInstallationTokenAsync(env.Project.GithubInstallationId!, ct); }
                catch (Exception ex) { throw new Exception($"GitHub App token: {ex.Message}", ex); }
            }

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
                GitToken: gitToken,
                DockerfilePath: env.Project.DockerfilePath,
                DockerContext: env.Project.DockerContext,
                RegistryUsername: env.Project.RegistryUsername,
                RegistryPassword: env.Project.RegistryPassword,
                EnvVars: env.EnvVars,
                Domains: env.Domains.Select(d => new DomainRouteRequest(d.Host, d.TargetPort, d.TargetService, d.IsStatic, d.StaticRoot, d.Weighted, 0, d.EnableHttps)).ToList());

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{agentUrl}/deploy") { Content = JsonContent.Create(payload) };
            var agentToken = config["Agent:Token"] ?? "";
            if (!string.IsNullOrEmpty(agentToken))
                reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
            var resp = await http.SendAsync(reqMsg, ct);
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
