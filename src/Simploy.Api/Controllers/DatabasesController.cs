using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Api.Services;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/databases"), Authorize]
public class DatabasesController(SimployDbContext db, DatabaseService dbService) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<Database>> List() =>
        await db.Databases.Include(d => d.Server).OrderByDescending(d => d.CreatedAt).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<Database>> Create(CreateDatabaseRequest req)
    {
        if (await db.Servers.FindAsync(req.ServerId) is null) return NotFound("Server not found");
        if (await db.Databases.AnyAsync(d => d.ServerId == req.ServerId && d.Name == req.Name))
            return Conflict("A database with that name already exists on this server");

        var (port, username, databaseName) = req.Type.ToLowerInvariant() switch
        {
            "mysql" => (3306, "app", req.Name),
            "mssql" => (1433, "sa", req.Name),
            "redis" => (6379, "", req.Name),
            "mongodb" => (27017, "root", req.Name),
            _ => (5432, "app", req.Name),
        };
        var d = new Database
        {
            Name = req.Name,
            Type = req.Type ?? "postgres",
            Version = req.Version ?? "16",
            ServerId = req.ServerId,
            Slot = "db",
            Username = username,
            Password = GeneratePassword(),
            DatabaseName = databaseName,
            Port = port,
            DataPath = string.IsNullOrWhiteSpace(req.DataPath) ? null : req.DataPath.Trim(),
            Status = "Pending",
        };
        db.Databases.Add(d);
        await db.SaveChangesAsync();
        _ = dbService.EnqueueAsync(d.Id);
        return CreatedAtAction(nameof(Get), new { id = d.Id }, d);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Database>> Get(Guid id) =>
        await db.Databases.Include(d => d.Server).FirstOrDefaultAsync(d => d.Id == id) is { } d ? d : NotFound();

    /// <summary>Re-provisions every managed database (bring all running resources up).</summary>
    [HttpPost("run-all")]
    public async Task<IActionResult> RunAll(CancellationToken ct)
    {
        var dbs = await db.Databases.Where(x => x.Status != "Removed" && x.Status != "Failed").ToListAsync(ct);
        foreach (var d in dbs) { d.Status = "Pending"; _ = dbService.EnqueueAsync(d.Id); }
        await db.SaveChangesAsync(ct);
        return Ok(new { queued = dbs.Count });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var d = await db.Databases.FindAsync(id);
        if (d is null) return NotFound();
        // Removes the container + data volume via the agent; the row is deleted on success.
        _ = dbService.EnqueueRemoveAsync(id);
        return NoContent();
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var sb = new StringBuilder(24);
        for (int i = 0; i < 24; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }
}

public record CreateDatabaseRequest(string Name, string Type, string Version, Guid ServerId, string? DataPath);
