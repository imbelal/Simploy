using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;
using Simploy.Shared.Models;

namespace Simploy.Api.Services;

/// <summary>Periodically backs up Simploy's own Postgres DB by asking the agent (which has the
/// docker socket) to pg_dump the control-plane's postgres container.</summary>
public class BackupWorker(IServiceProvider sp, IConfiguration config, ILogger<BackupWorker> log) : BackgroundService
{
    private static async Task<BackupSettings> EnsureSettingsAsync(SimployDbContext db, CancellationToken ct)
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
                var settings = await EnsureSettingsAsync(db, ct);
                var due = settings.Enabled && (settings.LastBackupAt is null
                    || DateTime.UtcNow - settings.LastBackupAt.Value >= TimeSpan.FromMinutes(settings.IntervalMinutes));
                if (due)
                {
                    var result = await RunBackupAsync(settings, ct);
                    settings.LastBackupAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    log.LogInformation("Backup taken: {Result}", result);
                }
            }
            catch (Exception ex) { log.LogError(ex, "Backup worker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    public static async Task<string> RunBackupAsync(BackupSettings s, CancellationToken ct)
    {
        var conn = ParseConnectionString();
        var agentUrl = System.Environment.GetEnvironmentVariable("Backup__AgentUrl") ?? "http://localhost:8089";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var token = System.Environment.GetEnvironmentVariable("Agent__Token") ?? "";
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var payload = new AgentBackupRequest(s.DbContainer, conn.database, conn.username, conn.password, s.DestDir, s.Retention);
        var resp = await http.PostAsJsonAsync($"{agentUrl}/system/backup", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    private static (string database, string username, string password) ParseConnectionString()
    {
        var cs = System.Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "";
        var parts = cs.Split(';').Select(p => p.Split('=', 2)).Where(a => a.Length == 2)
            .ToDictionary(a => a[0].Trim(), a => a[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return (parts.GetValueOrDefault("Database") ?? "simploy",
                parts.GetValueOrDefault("Username") ?? "postgres",
                parts.GetValueOrDefault("Password") ?? "postgres");
    }
}
