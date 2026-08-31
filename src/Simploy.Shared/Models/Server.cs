namespace Simploy.Shared.Models;

public enum ServerStatus { Pending, Online, Offline, Unreachable }

public class Server
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Host { get; set; } = default!; // IP or hostname
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "root";
    public ServerStatus Status { get; set; } = ServerStatus.Pending;
    public string? AgentVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }

    public List<Environment> Environments { get; set; } = [];
}
