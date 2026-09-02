namespace Simploy.Shared.Models;

/// <summary>Configures periodic backups of Simploy's own control-plane database.</summary>
public class BackupSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 1440; // default: daily
    public int Retention { get; set; } = 7;          // keep last N dumps
    public string DestDir { get; set; } = "/opt/simploy/backups";
    public string DbContainer { get; set; } = "simploy-postgres-1";
    public DateTime? LastBackupAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
