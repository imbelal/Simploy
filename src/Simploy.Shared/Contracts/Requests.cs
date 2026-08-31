namespace Simploy.Shared.Contracts;

public record CreateProjectRequest(string Name, string Slug, string ImageRepository, string? GitRepository, string? Description, string? GitToken, string? RegistryUsername, string? RegistryPassword, string? DockerfilePath, string? DockerContext);
public record CreateServerRequest(string Name, string Host, int SshPort, string SshUser);
public record CreateEnvironmentRequest(Guid ProjectId, Guid ServerId, string Name, string Slot, string ImageTag);
public record CreateDeploymentRequest(Guid EnvironmentId, string ImageTag, string Strategy, string? CommitSha, int CanaryPercent = 50);

/// <summary>
/// A domain mapped to an environment. Sent to the agent so it can render the
/// Caddyfile (including weighted canary split between old/new services).
/// </summary>
public record DomainRouteRequest(string Host, int? TargetPort, string? TargetService, bool IsStatic, string? StaticRoot, bool Weighted, int Weight);

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
    string? RegistryUsername,
    string? RegistryPassword,
    IReadOnlyDictionary<string, string>? EnvVars,
    IReadOnlyList<DomainRouteRequest>? Domains);
