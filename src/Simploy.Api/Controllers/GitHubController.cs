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

    /// <summary>Detects the Dockerfile path / template for a repo (used to prefill the form at import time).</summary>
    [HttpGet("detect")]
    public async Task<IActionResult> Detect([FromQuery] string repo, [FromQuery] string? installationId, CancellationToken ct)
    {
        try
        {
            var (df, template, compose) = await github.DetectProjectAsync(repo.Trim('/'), installationId, ct);
            return Ok(new { dockerfilePath = df, template, usesCompose = compose });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Lists the branches + default branch for a project's repo (for the env modal).</summary>
    [HttpGet("projects/{projectId:guid}/branches")]
    public async Task<IActionResult> Branches(Guid projectId, CancellationToken ct)
    {
        var p = await db.Projects.FindAsync(projectId);
        if (p?.GitRepository is null) return BadRequest(new { error = "Project has no git repo" });
        var repoStr = p.GitRepository.Trim();
        if (!repoStr.Contains("://")) repoStr = $"https://{repoStr}";
        try
        {
            var uri = new Uri(repoStr);
            var ownerRepo = uri.AbsolutePath.Trim('/');
            var (branches, def) = await github.GetBranchesAsync(ownerRepo, p.GithubInstallationId, ct);
            return Ok(new { branches, defaultBranch = def });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
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
