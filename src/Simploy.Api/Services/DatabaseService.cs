using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Simploy.Api.Data;
using Simploy.Shared.Contracts;

namespace Simploy.Api.Services;

public class DatabaseService(IServiceProvider sp, IConfiguration config, ILogger<DatabaseService> log)
{
    private readonly ConcurrentQueue<(Guid Id, bool Remove)> _queue = new();

    public Task EnqueueAsync(Guid id) { _queue.Enqueue((id, false)); return Task.CompletedTask; }
    public Task EnqueueRemoveAsync(Guid id) { _queue.Enqueue((id, true)); return Task.CompletedTask; }
    public bool TryDequeue(out (Guid, bool) item) => _queue.TryDequeue(out item);

    public async Task ExecuteAsync((Guid Id, bool Remove) job, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
        var item = await db.Databases.Include(d => d.Server).FirstOrDefaultAsync(d => d.Id == job.Id, ct);
        if (item?.Server is null) return;

        var payload = new AgentDbRequest(item.Name, item.Type, item.Version, item.Username, item.Password, item.DatabaseName, item.Port, item.Slot, item.DataPath);
        var agentUrl = $"http://{item.Server.Host}:8089";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var token = config["Agent:Token"] ?? "";
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var path = job.Remove ? "/db/remove" : "/db/deploy";
            var resp = await http.PostAsJsonAsync($"{agentUrl}{path}", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                item.Status = "Failed";
                await db.SaveChangesAsync(ct);
                return;
            }

            if (job.Remove)
            {
                // Container + data volume removed; drop the row so it disappears from the UI.
                db.Databases.Remove(item);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                item.Status = "Running";
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Database job for {Name} failed", item.Name);
            item.Status = "Failed";
            await db.SaveChangesAsync(ct);
        }
    }
}

public class DatabaseWorker(DatabaseService svc, ILogger<DatabaseWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (svc.TryDequeue(out var job))
                await svc.ExecuteAsync(job, ct);
            else
                await Task.Delay(1000, ct);
        }
    }
}
