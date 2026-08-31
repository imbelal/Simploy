using Microsoft.EntityFrameworkCore;
using Simploy.Shared.Models;

namespace Simploy.Api.Data;

public class SimployDbContext(DbContextOptions<SimployDbContext> options) : DbContext(options)
{
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Simploy.Shared.Models.Environment> Environments => Set<Simploy.Shared.Models.Environment>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<Deployment> Deployments => Set<Deployment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Server>().HasIndex(x => x.Host).IsUnique();
        b.Entity<Project>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<Simploy.Shared.Models.Environment>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        b.Entity<Domain>().HasIndex(x => x.Host).IsUnique();

        b.Entity<Simploy.Shared.Models.Environment>()
            .HasOne(e => e.Server).WithMany(s => s.Environments).HasForeignKey(e => e.ServerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Simploy.Shared.Models.Environment>()
            .HasOne(e => e.Project).WithMany(p => p.Environments).HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Domain>()
            .HasOne(d => d.Environment).WithMany(e => e.Domains).HasForeignKey(d => d.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Deployment>()
            .HasOne(d => d.Environment).WithMany(e => e.Deployments).HasForeignKey(d => d.EnvironmentId).OnDelete(DeleteBehavior.Cascade);

        // Store EnvVars as JSON
        b.Entity<Simploy.Shared.Models.Environment>().Property(e => e.EnvVars)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
    }
}
