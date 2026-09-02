namespace Simploy.Shared.Models;

/// <summary>A managed database provisioned on a server as its own container (Postgres /
/// MySQL / Redis), with a persistent volume, on the shared proxy network so apps
/// can reach it by name (Host=db-&lt;name&gt;). Used alongside apps that ship their own DB.</summary>
public class Database
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;   // used as the network alias: db-<name>
    public string Type { get; set; } = "postgres"; // postgres | mysql | redis | mongodb
    public string Version { get; set; } = "16";

    public Guid ServerId { get; set; }
    public Server? Server { get; set; }

    public string Slot { get; set; } = "db";
    public string Username { get; set; } = default!;
    public string Password { get; set; } = default!; // auto-generated
    public string DatabaseName { get; set; } = default!;
    public int Port { get; set; } = 5432;

    /// <summary>Optional host directory for the data (bind mount). Empty = Docker named volume.</summary>
    public string? DataPath { get; set; }

    public string Status { get; set; } = "Pending"; // Pending | Running | Failed | Removed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
