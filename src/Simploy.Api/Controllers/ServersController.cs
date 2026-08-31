using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/servers")]
public class ServersController(SimployDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<Server>> List() => await db.Servers.ToListAsync();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Server>> Get(Guid id) => await db.Servers.FindAsync(id) is { } s ? s : NotFound();

    /// <summary>Returns the agent one-liner installer: <c>curl .../api/servers/install | bash</c>.</summary>
    [HttpGet("install")]
    public IActionResult InstallScript()
    {
        var asm = typeof(ServersController).Assembly;
        using var s = asm.GetManifestResourceStream("install.sh");
        if (s is null) return NotFound("install script not bundled");
        using var r = new StreamReader(s);
        return Content(r.ReadToEnd(), "text/x-shellscript", System.Text.Encoding.UTF8);
    }

    [HttpPost]
    public async Task<ActionResult<Server>> Create(CreateServerRequest req)
    {
        var s = new Server { Name = req.Name, Host = req.Host, SshPort = req.SshPort, SshUser = req.SshUser };
        db.Servers.Add(s);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = s.Id }, s);
    }

    [HttpPost("{id:guid}/check")]
    public async Task<ActionResult<object>> Check(Guid id)
    {
        var s = await db.Servers.FindAsync(id);
        if (s is null) return NotFound();
        // v1: TCP ping to agent :8089/health - real agent will expose this
        using var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var resp = await c.GetAsync($"http://{s.Host}:8089/health");
            s.Status = resp.IsSuccessStatusCode ? ServerStatus.Online : ServerStatus.Offline;
            s.LastSeenAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return new { status = s.Status.ToString(), code = (int)resp.StatusCode };
        }
        catch (Exception ex)
        {
            s.Status = ServerStatus.Unreachable;
            await db.SaveChangesAsync();
            return new { status = s.Status.ToString(), error = ex.Message };
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var s = await db.Servers.FindAsync(id);
        if (s is null) return NotFound();
        db.Remove(s);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
