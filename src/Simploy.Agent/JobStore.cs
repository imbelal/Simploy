using System.Collections.Concurrent;
using System.Text;

namespace Simploy.Agent;

public class DeploymentJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public StringBuilder Logs { get; } = new();
    public string Status { get; set; } = "Running";   // Running | Success | Failed
    public bool Done { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public static class JobStore
{
    private static readonly ConcurrentDictionary<string, DeploymentJob> Jobs = new();
    private static readonly int MaxJobs = 200;

    public static DeploymentJob Create() => Add(new DeploymentJob());

    public static DeploymentJob Add(DeploymentJob job)
    {
        Jobs[job.Id] = job;
        // prune old
        foreach (var kv in Jobs.Where(kv => kv.Value.Done && DateTime.UtcNow - kv.Value.StartedAt > TimeSpan.FromMinutes(30)))
            Jobs.TryRemove(kv.Key, out _);
        if (Jobs.Count > MaxJobs)
        {
            var oldest = Jobs.Values.OrderBy(j => j.StartedAt).FirstOrDefault();
            if (oldest != null) Jobs.TryRemove(oldest.Id, out _);
        }
        return job;
    }

    public static DeploymentJob? Get(string id) => Jobs.TryGetValue(id, out var j) ? j : null;
}

/// <summary>Captures all build/deploy command output into the current background job.</summary>
public static class DeployLog
{
    private static readonly AsyncLocal<DeploymentJob?> Current = new();
    public static DeploymentJob? CurrentJob { get => Current.Value; set => Current.Value = value; }
    public static void Write(string line) { if (Current.Value != null) Current.Value.Logs.AppendLine(line); }
}
