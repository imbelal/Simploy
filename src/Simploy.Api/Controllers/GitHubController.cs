using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Api.Services;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/github"), Authorize]
public class GitHubController(SimployDbContext db, GitHubAppService github) : ControllerBase
{
    /// <summary>Returns the URL to install the Simploy GitHub App (the UI opens it).</summary>
    [HttpGet("install")]
    public IActionResult Install() =>
        github.IsConfigured ? Ok(new { url = github.InstallUrl })
        : BadRequest(new { error = "GitHub App is not configured on this instance" });

    /// <summary>GitHub redirects here after an install (Setup URL); confirm and go back.</summary>
    [HttpGet("callback"), AllowAnonymous]
    public IActionResult Callback([FromQuery] string? installation_id)
    {
        return Content($"<h2>GitHub App installed</h2><p>Installation #{installation_id}. Go back to Simploy → Projects → bind this installation.</p>", "text/html");
    }

    /// <summary>Lists the installations the app can use, so a project can be bound.</summary>
    [HttpGet("installations")]
    public async Task<IActionResult> Installations(CancellationToken ct)
    {
        if (!github.IsConfigured) return BadRequest(new { error = "GitHub App is not configured" });
        return Ok(await github.ListInstallationsAsync(ct));
    }

    /// <summary>Lists the repos an installation can access (for the project-import picker).</summary>
    [HttpGet("repositories")]
    public async Task<IActionResult> Repositories([FromQuery] string installationId, CancellationToken ct)
    {
        if (!github.IsConfigured) return BadRequest(new { error = "GitHub App is not configured" });
        return Ok(await github.ListRepositoriesAsync(installationId, ct));
    }

    /// <summary>Binds a project to a GitHub App installation (used to mint tokens on deploy).</summary>
    [HttpPut("projects/{projectId:guid}/installation")]
    public async Task<IActionResult> BindProject(Guid projectId, BindInstallationRequest req)
    {
        var p = await db.Projects.FindAsync(projectId);
        if (p is null) return NotFound();
        p.GithubInstallationId = req.InstallationId;
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record BindInstallationRequest(string InstallationId);
