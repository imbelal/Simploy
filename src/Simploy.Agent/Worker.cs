namespace Simploy.Agent;

public class Worker(DockerService docker, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Simploy Agent started, listening on :8089, docker.sock {Exists}", File.Exists("/var/run/docker.sock"));

        // Ensure the shared proxy network + Caddy (+ control-plane domain fragment) exist
        // on startup, not just during a deploy.
        try { await docker.EnsureProxyAsync(stoppingToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not ensure shared proxy: {Ex}", ex.Message); }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            logger.LogDebug("Agent heartbeat {Time}", DateTimeOffset.Now);
        }
    }
}
