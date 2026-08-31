using Simploy.Shared.Models;

namespace Simploy.Api.Data;

public static class SeedData
{
    /// <summary>
    /// Seeds a small demo setup on first run so the UI isn't empty and the
    /// onboarding guide has something to point at. Skips if data already exists.
    /// </summary>
    public static void EnsureSeeded(SimployDbContext db)
    {
        if (db.Servers.Any() || db.Projects.Any()) return;

        // Distinct hosts: there's a unique index on Server.Host, so the demo
        // servers can't share an IP. 127.0.0.1 / 127.0.0.2 are both loopback.
        var stagingVm = new Server { Name = "staging-vm", Host = "127.0.0.1", SshPort = 22, SshUser = "root", Status = ServerStatus.Pending };
        var prodVm = new Server { Name = "prod-vm", Host = "127.0.0.2", SshPort = 22, SshUser = "root", Status = ServerStatus.Pending };
        db.Servers.AddRange(stagingVm, prodVm);

        var project = new Project
        {
            Name = "BdShopManager",
            Slug = "bdshopmanager",
            ImageRepository = "ghcr.io/imbelal/bdshopmanager",
            GitRepository = "https://github.com/imbelal/BdShopManager",
            DockerfilePath = "src/WebApi/Dockerfile",
            DockerContext = "."
        };
        db.Projects.Add(project);

        var staging = new Simploy.Shared.Models.Environment { ProjectId = project.Id, ServerId = stagingVm.Id, Name = "staging", Slot = "staging", ImageTag = "staging", Branch = "main" };
        var prod = new Simploy.Shared.Models.Environment { ProjectId = project.Id, ServerId = prodVm.Id, Name = "production", Slot = "prod", ImageTag = "prod", Branch = "main" };
        db.Environments.AddRange(staging, prod);

        db.Deployments.Add(new Deployment
        {
            EnvironmentId = prod.Id,
            ImageTag = "prod",
            Strategy = DeploymentStrategy.Recreate,
            Status = DeploymentStatus.Healthy,
            CanaryPercent = 0,
            TriggeredBy = "seed"
        });

        db.SaveChanges();
    }
}
