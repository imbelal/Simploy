namespace Simploy.Shared.Contracts;

public record CreateProjectRequest(string Name, string Slug, string ImageRepository, string? GitRepository, string? Description, string? GitToken, string? RegistryUsername, string? RegistryPassword, string? DockerfilePath, string? DockerContext);
public record CreateServerRequest(string Name, string Host, int SshPort, string SshUser);
public record CreateEnvironmentRequest(Guid ProjectId, Guid ServerId, string Name, string Slot, string ImageTag, string? Branch = null);
public record SetEnvVarsRequest(Dictionary<string, string> EnvVars);
public record SetDomainsRequest(List<DomainRouteRequest> Domains);
public record CreateDeploymentRequest(Guid EnvironmentId, string ImageTag, string Strategy, string? CommitSha, int CanaryPercent = 50);
public record LoginRequest(string Username, string Password);

/// <summary>Provisions a managed database container on the agent.</summary>
public record AgentDbRequest(string DbName, string Type, string Version, string Username, string Password, string DatabaseName, int Port, string Slot, string? DataPath = null);

/// <summary>Backs up Simploy's control-plane Postgres via the agent (docker exec pg_dump).</summary>
public record AgentBackupRequest(string Container, string DatabaseName, string Username, string Password, string DestDir, int Retention);

/// <summary>
/// A domain mapped to an environment. Sent to the agent so it can render the
/// Caddyfile (including weighted canary split between old/new services).
/// </summary>
public record DomainRouteRequest(string Host, int? TargetPort, string? TargetService, bool IsStatic, string? StaticRoot, bool Weighted, int Weight, bool EnableHttps = false);

/// <summary>
/// Full deploy payload sent from the control plane (Api) to the Agent.
/// The agent renders compose/.env/Caddyfile, builds from git (if provided),
/// tags the image, runs it and gates on health.
/// </summary>
public record AgentDeployRequest(
    string ProjectSlug,
    string Slot,
    string ImageRepository,
    string ImageTag,
    string Strategy,
    int CanaryPercent,
    string? PreviousImageTag,
    string? GitRepository,
    string? GitBranch,
    string? GitToken,
    string? DockerfilePath,
    string? DockerContext,
    string? Template,
    string? RegistryUsername,
    string? RegistryPassword,
    IReadOnlyDictionary<string, string>? EnvVars,
    IReadOnlyList<DomainRouteRequest>? Domains);
