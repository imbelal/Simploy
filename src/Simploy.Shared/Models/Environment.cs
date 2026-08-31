namespace Simploy.Shared.Models;

public class Environment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = default!;

    public Guid ServerId { get; set; }
    public Server Server { get; set; } = default!;

    public string Name { get; set; } = default!; // production, staging
    public string Slot { get; set; } = default!; // prod, staging -> used as compose -p
    public string? Branch { get; set; } = "main";
    public string ImageTag { get; set; } = "prod"; // prod / staging

    // Caddy / routing
    public List<Domain> Domains { get; set; } = [];

    // Env vars are stored encrypted / per-environment
    public Dictionary<string, string> EnvVars { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Deployment> Deployments { get; set; } = [];
}
