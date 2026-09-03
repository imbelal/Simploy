namespace Simploy.Shared.Models;

/// <summary>A single uptime probe of an environment's public URL. Appended by the
/// control-plane UptimeWorker so the dashboard can show up%/latency per domain.</summary>
public class UptimeCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public string Url { get; set; } = default!;
    public bool Ok { get; set; }
    public int? StatusCode { get; set; }
    public int LatencyMs { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
