namespace Simploy.Shared.Models;

public class Domain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public Environment Environment { get; set; } = default!;

    public string Host { get; set; } = default!; // bdshopapi.imbelal.com
    public int? TargetPort { get; set; } // 8080
    public string? TargetService { get; set; } // webapi:8080
    public bool IsStatic { get; set; }
    public string? StaticRoot { get; set; } // /web
    public bool Weighted { get; set; } // for canary
    public bool EnableHttps { get; set; } // Caddy automatic HTTPS/Let's Encrypt

    public bool IsActive { get; set; } = true;
}
