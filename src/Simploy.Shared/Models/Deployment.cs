namespace Simploy.Shared.Models;

public enum DeploymentStatus { Queued, Building, Deploying, Healthy, Failed, RolledBack }
public enum DeploymentStrategy { Recreate, Canary }

public class Deployment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public Environment Environment { get; set; } = default!;

    public string ImageTag { get; set; } = default!; // prod-abc123
    public string? CommitSha { get; set; }
    public DeploymentStrategy Strategy { get; set; } = DeploymentStrategy.Recreate;
    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;

    // Canary state
    public int CanaryPercent { get; set; }
    public string? CanaryStep { get; set; } // up, set, complete, rollback

    public string? LogOutput { get; set; }
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string TriggeredBy { get; set; } = "manual";
}
