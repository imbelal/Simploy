namespace Simploy.Shared.Models;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!; // bdshopmanager
    public string? Description { get; set; }
    public string? GitRepository { get; set; } // https://github.com/imbelal/BdShopManager
    public string ImageRepository { get; set; } = default!; // ghcr.io/imbelal/bdshopmanager
    public string DockerfilePath { get; set; } = "src/WebApi/Dockerfile";
    public string? DockerContext { get; set; } = ".";
    // Private repo / registry auth (stored encrypted in prod, plaintext for demo)
    public string? GitToken { get; set; } // PAT classic with repo scope for private clone
    public string? RegistryUsername { get; set; } // ghcr user (for docker login)
    public string? RegistryPassword { get; set; } // GHCR PAT (ghcr_pat)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Environment> Environments { get; set; } = [];
}
