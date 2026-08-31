using Simploy.Shared.Models;

namespace Simploy.Shared.Contracts;

/// <summary>
/// Deployment summary returned by the API. Includes just enough navigation
/// (server + first static domain) to build an "access URL" for the running app,
/// without leaking the environment's env vars / secrets.
/// </summary>
public record DeploymentDto(
    Guid Id,
    Guid EnvironmentId,
    string EnvironmentName,
    string? ServerName,
    string? ServerHost,
    string ImageTag,
    string? CommitSha,
    DeploymentStrategy Strategy,
    DeploymentStatus Status,
    int CanaryPercent,
    string? LogOutput,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string TriggeredBy,
    string? AccessUrl);

public static class DeploymentDtoMapper
{
    public static DeploymentDto ToDto(this Deployment d)
    {
        var server = d.Environment?.Server;
        var domains = d.Environment?.Domains;
        var firstDomain = domains?.FirstOrDefault(x => !x.IsStatic && !string.IsNullOrWhiteSpace(x.Host));
        var portDomain = domains?.FirstOrDefault(x => !x.IsStatic && x.TargetPort.HasValue);
        var port = portDomain?.TargetPort ?? 8080;

        // Prefer a real domain; otherwise fall back to the server IP and the app port.
        string? url = null;
        if (!string.IsNullOrWhiteSpace(firstDomain?.Host))
            url = $"http://{firstDomain.Host}";
        else if (!string.IsNullOrWhiteSpace(server?.Host))
            url = $"http://{server.Host}:{port}";

        return new DeploymentDto(
            d.Id,
            d.EnvironmentId,
            d.Environment?.Name ?? "",
            server?.Name,
            server?.Host,
            d.ImageTag,
            d.CommitSha,
            d.Strategy,
            d.Status,
            d.CanaryPercent,
            d.LogOutput,
            d.Error,
            d.CreatedAt,
            d.StartedAt,
            d.FinishedAt,
            d.TriggeredBy,
            url);
    }
}
