using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Api.Services;
using Simploy.Shared.Models;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/settings/backups"), Authorize]
public class BackupsController(SimployDbContext db, IConfiguration config) : ControllerBase
{
    private static async Task<BackupSettings> EnsureAsync(SimployDbContext db)
    {
        if (await db.BackupSettings.FirstOrDefaultAsync() is { } s) return s;
        var s2 = new BackupSettings();
        db.BackupSettings.Add(s2);
        await db.SaveChangesAsync();
        return s2;
    }

    [HttpGet]
    public async Task<BackupSettings> Get() => await EnsureAsync(db);

    [HttpPut]
    public async Task<BackupSettings> Put(BackupSettings req)
    {
        var s = await EnsureAsync(db);
        s.Enabled = req.Enabled;
        s.IntervalMinutes = Math.Max(5, req.IntervalMinutes);
        s.Retention = Math.Max(1, req.Retention);
        s.DestDir = string.IsNullOrWhiteSpace(req.DestDir) ? s.DestDir : req.DestDir;
        s.DbContainer = string.IsNullOrWhiteSpace(req.DbContainer) ? s.DbContainer : req.DbContainer;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return s;
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var s = await EnsureAsync(db);
        var result = await BackupWorker.RunBackupAsync(s, ct);
        s.LastBackupAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { result });
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var s = await EnsureAsync(db);
        var agentUrl = config["Backup:AgentUrl"] ?? "http://localhost:8089";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var token = config["Agent:Token"] ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{agentUrl}/system/backup/list?dir={Uri.EscapeDataString(s.DestDir)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return Content(body, "application/json");
    }
}
