using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Api.Services;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/deployments")]
public class DeploymentsController(SimployDbContext db, DeploymentService deployer) : ControllerBase
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

    [HttpPost("{id:guid}/rollback")]
    public async Task<IActionResult> Rollback(Guid id)
    {
        var d = await db.Deployments.FindAsync(id);
        if (d is null) return NotFound();
        var rollback = new Deployment { EnvironmentId = d.EnvironmentId, ImageTag = d.ImageTag, Strategy = DeploymentStrategy.Recreate, Status = DeploymentStatus.Queued };
        db.Deployments.Add(rollback);
        await db.SaveChangesAsync();
        _ = deployer.EnqueueAsync(rollback.Id);
        return AcceptedAtAction(nameof(Get), new { id = rollback.Id }, rollback);
    }
}
