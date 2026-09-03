using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Services;

/// <summary>Runs pg_dump on Simploy's own control-plane DB via the agent on the same host.</summary>
public class BackupService(IConfiguration config)
{
    public string AgentUrl => config["Backup:AgentUrl"] ?? "http://host.docker.internal:8089";

    public async Task<string> RunAsync(BackupSettings s, CancellationToken ct)
    {
        var conn = Parse(System.Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? config.GetConnectionString("Default"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var token = config["Agent:Token"] ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var payload = new AgentBackupRequest(s.DbContainer, conn.database, conn.username, conn.password, s.DestDir, s.Retention);
        var resp = await http.PostAsJsonAsync($"{AgentUrl}/system/backup", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    public async Task<string> ListAsync(string destDir, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var token = config["Agent:Token"] ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{AgentUrl}/system/backup/list?dir={Uri.EscapeDataString(destDir)}", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Restores a backup file into the control-plane Postgres (via the agent).</summary>
    public async Task<string> RestoreAsync(BackupSettings s, string file, CancellationToken ct)
    {
        var conn = Parse(System.Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? config.GetConnectionString("Default"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var token = config["Agent:Token"] ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var payload = new AgentBackupRequest(s.DbContainer, conn.database, conn.username, conn.password, file, s.Retention);
        var resp = await http.PostAsJsonAsync($"{AgentUrl}/system/backup/restore", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    private static (string database, string username, string password) Parse(string? cs)
    {
        cs ??= "";
        var parts = cs.Split(';').Select(p => p.Split('=', 2)).Where(a => a.Length == 2)
            .ToDictionary(a => a[0].Trim(), a => a[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return (parts.GetValueOrDefault("Database") ?? "simploy",
                parts.GetValueOrDefault("Username") ?? "postgres",
                parts.GetValueOrDefault("Password") ?? "postgres");
    }
}

public class BackupWorker(IServiceProvider sp, BackupService backup, ILogger<BackupWorker> log) : BackgroundService
{
    private static async Task<BackupSettings> EnsureAsync(SimployDbContext db, CancellationToken ct)
    {
        if (await db.BackupSettings.FirstOrDefaultAsync(ct) is { } s) return s;
        var s2 = new BackupSettings();
        db.BackupSettings.Add(s2);
        await db.SaveChangesAsync(ct);
        return s2;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
                var s = await EnsureAsync(db, ct);
                var due = s.Enabled && (s.LastBackupAt is null
                    || DateTime.UtcNow - s.LastBackupAt.Value >= TimeSpan.FromMinutes(s.IntervalMinutes));
                if (due)
                {
                    var result = await backup.RunAsync(s, ct);
                    s.LastBackupAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    log.LogInformation("Backup taken: {Result}", result);
                }
            }
            catch (Exception ex) { log.LogError(ex, "Backup worker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
