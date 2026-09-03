using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;

namespace Simploy.Api.Services;

/// <summary>Periodically probes each environment's public domain(s) and records the result
/// (status code + latency) in UptimeChecks so the dashboard can show up% and latency.</summary>
public class UptimeWorker(IServiceProvider sp, ILogger<UptimeWorker> log) : BackgroundService
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // small initial delay so we don't race the DB on boot
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();

                var targets = await db.Domains
                    .AsNoTracking()
                    .Where(d => d.IsActive)
                    .Select(d => new { d.EnvironmentId, d.Host })
                    .ToListAsync(ct);

                foreach (var t in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    var url = t.Host.StartsWith("http") ? t.Host : $"https://{t.Host}";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok; int? code = null;
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.UserAgent.ParseAdd("Simploy-Uptime/1.0");
                        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        code = (int)resp.StatusCode;
                        ok = code >= 200 && code < 500;
                    }
                    catch { ok = false; }
                    finally { sw.Stop(); }

                    db.UptimeChecks.Add(new Simploy.Shared.Models.UptimeCheck
                    {
                        EnvironmentId = t.EnvironmentId,
                        Url = url,
                        Ok = ok,
                        StatusCode = code,
                        LatencyMs = (int)sw.ElapsedMilliseconds,
                    });
                }

                if (targets.Count > 0)
                {
                    await db.SaveChangesAsync(ct);
                    // Trim to last 500 checks per env to keep the table bounded.
                    await db.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"UptimeChecks\" WHERE \"Id\" IN (SELECT \"Id\" FROM (SELECT \"Id\", ROW_NUMBER() OVER (PARTITION BY \"EnvironmentId\" ORDER BY \"CheckedAt\" DESC) rn FROM \"UptimeChecks\") x WHERE rn > 500)", ct);
                }
            }
            catch (Exception ex) { log.LogError(ex, "Uptime worker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
