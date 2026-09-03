using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/uptime"), Authorize]
public class UptimeController(SimployDbContext db) : ControllerBase
{
    /// <summary>Returns per-environment uptime summary from recent UptimeChecks: current
    /// status, up% over the last 100 checks, last latency, and the 30 most recent checks.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var checks = await db.UptimeChecks
            .AsNoTracking()
            .OrderByDescending(c => c.CheckedAt)
            .Take(1000)
            .ToListAsync(ct);

        var byEnv = checks
            .GroupBy(c => c.EnvironmentId)
            .Select(g => new
            {
                environmentId = g.Key,
                url = g.First().Url,
                last = g.First(),
                upPct = Math.Round(g.Take(100).Count(x => x.Ok) * 100.0 / (g.Take(100).Count() == 0 ? 1 : g.Take(100).Count()), 1),
            })
            .OrderByDescending(x => x.last.CheckedAt)
            .ToList();

        return Ok(byEnv);
    }
}
