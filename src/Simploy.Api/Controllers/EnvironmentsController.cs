using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/environments")]
public class EnvironmentsController(SimployDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<Shared.Models.Environment>> List([FromQuery] Guid? projectId) =>
        await db.Environments.Include(e => e.Server).Include(e => e.Domains)
            .Where(e => projectId == null || e.ProjectId == projectId).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<Shared.Models.Environment>> Create(CreateEnvironmentRequest req)
    {
        var e = new Shared.Models.Environment { ProjectId = req.ProjectId, ServerId = req.ServerId, Name = req.Name, Slot = req.Slot, ImageTag = req.ImageTag };
        db.Environments.Add(e);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = e.Id }, e);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Shared.Models.Environment>> Get(Guid id) =>
        await db.Environments.Include(e => e.Server).Include(e => e.Domains).Include(e => e.Deployments.OrderByDescending(d => d.CreatedAt).Take(10))
            .FirstOrDefaultAsync(e => e.Id == id) is { } e ? e : NotFound();

    /// <summary>Sets the env vars passed to the app as a .env file during deploy.</summary>
    [HttpPut("{id:guid}/env-vars")]
    public async Task<IActionResult> SetEnvVars(Guid id, SetEnvVarsRequest req)
    {
        var e = await db.Environments.FindAsync(id);
        if (e is null) return NotFound();
        e.EnvVars = req.EnvVars;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Sets the domains routed to this environment via Caddy.</summary>
    [HttpPut("{id:guid}/domains")]
    public async Task<IActionResult> SetDomains(Guid id, SetDomainsRequest req)
    {
        var e = await db.Environments.Include(x => x.Domains).FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return NotFound();
        var now = Guid.NewGuid();
        e.Domains = req.Domains.Select(d => new Shared.Models.Domain
        {
            Id = Guid.NewGuid(),
            EnvironmentId = id,
            Host = d.Host,
            TargetPort = d.TargetPort,
            TargetService = d.TargetService,
            IsStatic = d.IsStatic,
            StaticRoot = d.StaticRoot,
            Weighted = d.Weighted,
            IsActive = true
        }).ToList();
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var e = await db.Environments.FindAsync(id);
        if (e is null) return NotFound();
        db.Remove(e);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
