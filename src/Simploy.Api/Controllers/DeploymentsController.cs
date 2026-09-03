using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Api.Services;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/deployments"), Authorize]
public class DeploymentsController(SimployDbContext db, DeploymentService deployer, IConfiguration config) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<DeploymentDto>> List([FromQuery] Guid? environmentId)
    {
        var items = await db.Deployments
            .Include(d => d.Environment).ThenInclude(e => e.Server)
            .Include(d => d.Environment).ThenInclude(e => e.Domains)
            .Where(d => environmentId == null || d.EnvironmentId == environmentId)
            .OrderByDescending(d => d.CreatedAt).Take(50)
            .ToListAsync();
        return items.Select(d => d.ToDto());
    }

    [HttpPost]
    public async Task<ActionResult<Deployment>> Create(CreateDeploymentRequest req)
    {
        var env = await db.Environments.Include(e => e.Server).Include(e => e.Project).FirstOrDefaultAsync(e => e.Id == req.EnvironmentId);
        if (env is null) return NotFound("Environment not found");
        var strategy = Enum.TryParse<DeploymentStrategy>(req.Strategy, true, out var s) ? s : DeploymentStrategy.Recreate;
        var d = new Deployment { EnvironmentId = req.EnvironmentId, ImageTag = req.ImageTag, CommitSha = req.CommitSha, Strategy = strategy, CanaryPercent = strategy == DeploymentStrategy.Canary ? req.CanaryPercent : 0 };
        db.Deployments.Add(d);
        await db.SaveChangesAsync();
        _ = deployer.EnqueueAsync(d.Id); // fire-and-forget, background service does work
        return CreatedAtAction(nameof(Get), new { id = d.Id }, d);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeploymentDto>> Get(Guid id)
    {
        var d = await db.Deployments.Include(d => d.Environment).ThenInclude(e => e.Server)
            .Include(d => d.Environment).ThenInclude(e => e.Domains)
            .FirstOrDefaultAsync(d => d.Id == id);
        return d is null ? NotFound() : d.ToDto();
    }

    [HttpPost("run-all")]
    public async Task<IActionResult> RunAll(CancellationToken ct)
    {
        var envs = await db.Environments.Include(e => e.Project).ToListAsync(ct);
        var queued = 0;
        foreach (var env in envs)
        {
            var d = new Deployment { EnvironmentId = env.Id, ImageTag = env.ImageTag, Strategy = DeploymentStrategy.Recreate, Status = DeploymentStatus.Queued };
            db.Deployments.Add(d);
            queued++;
            _ = deployer.EnqueueAsync(d.Id);
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { queued });
    }

    [HttpPost("{id:guid}/rollback")]
    public async Task<IActionResult> Rollback(Guid id)
    {
        var d = await db.Deployments.FindAsync(id);
        if (d is null) return NotFound();
        // Roll back to the last healthy image for this environment (different tag than the current one).
        var lastGood = await db.Deployments
            .Where(x => x.EnvironmentId == d.EnvironmentId && x.Status == DeploymentStatus.Healthy
                && x.ImageTag != d.ImageTag && x.Id != d.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ImageTag)
            .FirstOrDefaultAsync();
        if (lastGood is null) return BadRequest(new { error = "No previous healthy deployment to roll back to" });

        var rollback = new Deployment { EnvironmentId = d.EnvironmentId, ImageTag = lastGood, Strategy = DeploymentStrategy.Recreate, Status = DeploymentStatus.Queued, TriggeredBy = "rollback" };
        db.Deployments.Add(rollback);
        await db.SaveChangesAsync();
        _ = deployer.EnqueueAsync(rollback.Id);
        return AcceptedAtAction(nameof(Get), new { id = rollback.Id }, rollback);
    }

    /// <summary>Server-Sent Events stream of a deployment's live build logs.</summary>
    [HttpGet("{id:guid}/logs/stream")]
    public async Task StreamLogs(Guid id, CancellationToken ct)
    {
        var d = await db.Deployments.Include(x => x.Environment).ThenInclude(e => e.Server)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d?.AgentJobId is null || d.Environment?.Server is null) { Response.StatusCode = 404; return; }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(ct);

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var token = config["Agent:Token"] ?? "";
        var agentUrl = $"http://{d.Environment.Server.Host}:8089/deploy/{d.AgentJobId}";
        var last = "";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, agentUrl);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var logs = root.GetProperty("logs").GetString() ?? "";
                if (logs.Length > last.Length)
                {
                    var newText = logs[last.Length..];
                    foreach (var line in newText.Split('\n'))
                        if (line.Length > 0)
                        {
                            await Response.WriteAsync($"event: log\ndata: {line}\n\n", ct);
                            await Response.Body.FlushAsync(ct);
                        }
                    last = logs;
                }
                if (root.GetProperty("done").GetBoolean())
                {
                    var st = root.TryGetProperty("status", out var s) ? s.GetString() : "";
                    await Response.WriteAsync($"event: done\ndata: {{\"status\":\"{st}\"}}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    break;
                }
            }
            catch { /* transient */ }
            await Task.Delay(500, ct);
        }
    }
}
