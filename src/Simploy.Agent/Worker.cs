namespace Simploy.Agent;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Simploy Agent started, listening on :8089, docker.sock {Exists}", File.Exists("/var/run/docker.sock"));
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            logger.LogDebug("Agent heartbeat {Time}", DateTimeOffset.Now);
        }
    }
}
