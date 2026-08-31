using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/projects")]
public class ProjectsController(SimployDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<Project>> List() =>
        await db.Projects.Include(p => p.Environments).ThenInclude(e => e.Server).ToListAsync();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Project>> Get(Guid id) =>
        await db.Projects.Include(p => p.Environments).ThenInclude(e => e.Domains).FirstOrDefaultAsync(p => p.Id == id)
        is { } p ? p : NotFound();

    [HttpPost]
    public async Task<ActionResult<Project>> Create(CreateProjectRequest req)
    {
        var p = new Project { Name = req.Name, Slug = req.Slug, ImageRepository = req.ImageRepository, GitRepository = req.GitRepository, Description = req.Description, GitToken = req.GitToken, RegistryUsername = req.RegistryUsername, RegistryPassword = req.RegistryPassword, DockerfilePath = req.DockerfilePath ?? "Dockerfile", DockerContext = req.DockerContext ?? "." };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var p = await db.Projects.FindAsync(id);
        if (p is null) return NotFound();
        db.Remove(p);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
