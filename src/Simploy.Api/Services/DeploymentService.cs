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

            // Prefer the GitHub App (short-lived token) when a project is bound to one;
            // fall back to the PAT only if no installation is bound.
            var gitToken = env.Project.GitToken;
            if (!string.IsNullOrEmpty(env.Project.GithubInstallationId) && github.IsConfigured)
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
                Template: env.Project.Template,
                RegistryUsername: env.Project.RegistryUsername,
                RegistryPassword: env.Project.RegistryPassword,
                EnvVars: env.EnvVars,
                Domains: env.Domains.Select(d => new DomainRouteRequest(d.Host, d.TargetPort, d.TargetService, d.IsStatic, d.StaticRoot, d.Weighted, 0, d.EnableHttps)).ToList());

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{agentUrl}/deploy") { Content = JsonContent.Create(payload) };
            var agentToken = config["Agent:Token"] ?? "";
            if (!string.IsNullOrEmpty(agentToken))
                reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
            var resp = await http.SendAsync(reqMsg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                deployment.Status = DeploymentStatus.Failed;
                deployment.Error = $"Agent {resp.StatusCode}: {body}";
                deployment.FinishedAt = DateTime.UtcNow;
            }
            else
            {
                // Async job started: store its id; a poller streams logs + sets final status.
                var jobId = (body.Contains("\"jobId\"") ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body) : default);
                deployment.AgentJobId = jobId.ValueKind == System.Text.Json.JsonValueKind.Object && jobId.TryGetProperty("jobId", out var j) ? j.GetString() : null;
                deployment.LogOutput = null;
                deployment.Status = DeploymentStatus.Building;
                deployment.CanaryStep = deployment.Strategy == DeploymentStrategy.Canary ? $"canary {deployment.CanaryPercent}" : null;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Deploy {Id} failed to start", deploymentId);
            deployment.Status = DeploymentStatus.Failed;
            deployment.Error = ex.Message;
            deployment.FinishedAt = DateTime.UtcNow;
        }
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

/// <summary>Polls in-flight deployments on the agent and streams logs + final status.</summary>
public class DeploymentPoller(IServiceProvider sp, IConfiguration config, ILogger<DeploymentPoller> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
                var inflight = await db.Deployments
                    .Include(d => d.Environment).ThenInclude(e => e.Server)
                    .Where(d => d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Deploying)
                    .Where(d => d.AgentJobId != null)
                    .Take(10).ToListAsync(ct);

                foreach (var d in inflight)
                {
                    try { await PollAsync(d, scope, ct); }
                    catch (Exception ex) { log.LogWarning(ex, "Poll {Id} failed", d.Id); }
                }
            }
            catch (Exception ex) { log.LogError(ex, "Deployment poller tick failed"); }
            await Task.Delay(2000, ct);
        }
    }

    private async Task PollAsync(Deployment d, IServiceScope scope, CancellationToken ct)
    {
        var server = d.Environment?.Server;
        if (server is null) return;
        var svcName = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
        var deploySvc = scope.ServiceProvider.GetRequiredService<DeploymentService>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var token = config["Agent:Token"] ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"http://{server.Host}:8089/deploy/{d.AgentJobId}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return;

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("logs", out var logs)) d.LogOutput = Truncate(logs.GetString(), 100_000);
        if (root.TryGetProperty("error", out var err) && err.ValueKind != System.Text.Json.JsonValueKind.Null) d.Error = d.Error ?? err.GetString();
        var done = root.TryGetProperty("done", out var dn) && dn.GetBoolean();
        var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
        if (done)
        {
            d.Status = status == "Success" ? DeploymentStatus.Healthy : DeploymentStatus.Failed;
            if (d.Status == DeploymentStatus.Failed && string.IsNullOrEmpty(d.Error)) d.Error = d.LogOutput is { Length: > 0 } ? d.LogOutput[..Math.Min(d.LogOutput.Length, 500)] : "deploy failed";
            d.FinishedAt = DateTime.UtcNow;
            await svcName.SaveChangesAsync(ct);

            // Auto-rollback to the last healthy image when a non-rollback deploy fails.
            if (d.Status == DeploymentStatus.Failed && d.TriggeredBy != "rollback"
                && d.Environment?.AutoRollback == true)
            {
                var lastGood = await svcName.Deployments
                    .Where(x => x.EnvironmentId == d.EnvironmentId && x.Status == DeploymentStatus.Healthy
                        && x.ImageTag != d.ImageTag && x.Id != d.Id)
                    .OrderByDescending(x => x.CreatedAt).Select(x => x.ImageTag).FirstOrDefaultAsync(ct);
                if (lastGood is not null)
                {
                    var rb = new Deployment { EnvironmentId = d.EnvironmentId, ImageTag = lastGood, Strategy = DeploymentStrategy.Recreate, Status = DeploymentStatus.Queued, TriggeredBy = "rollback" };
                    svcName.Deployments.Add(rb);
                    await svcName.SaveChangesAsync(ct);
                    _ = deploySvc.EnqueueAsync(rb.Id);
                }
            }
        }
        else
        {
            await scope.ServiceProvider.GetRequiredService<SimployDbContext>().SaveChangesAsync(ct);
        }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[^max..]);
}
