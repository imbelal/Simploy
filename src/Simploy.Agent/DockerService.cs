using System.Diagnostics;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Simploy.Shared.Contracts;

namespace Simploy.Agent;

/// <summary>
/// Performs a deploy on the VM. Sources the app from a git repo (building the
/// image when a Dockerfile is present), otherwise pulls a prebuilt image, writes
/// a docker-compose.yml + .env + Caddyfile, runs the stack and gates on health.
/// </summary>
public class DockerService(ILogger<DockerService> log)
{
    private const string BaseDir = "/opt/simploy";

    private DockerClient Client => new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();

    public async Task<string> DeployAsync(AgentDeployRequest req, CancellationToken ct)
    {
        var slotDir = Path.Combine(BaseDir, ComposeRenderer.Sanitize(req.ProjectSlug), ComposeRenderer.Sanitize(req.Slot));
        var sourceDir = slotDir;
        Directory.CreateDirectory(slotDir);

        // ---- 1. Source the app: clone from git (build from source) or use what's already here.
        if (!string.IsNullOrWhiteSpace(req.GitRepository))
        {
            sourceDir = Path.Combine(slotDir, "src");
            Directory.CreateDirectory(sourceDir);
            await SourceFromGitAsync(req, sourceDir, ct);
        }

        var serviceName = ComposeRenderer.ServiceName(req.ProjectSlug);
        var image = $"{req.ImageRepository}:{req.ImageTag}";
        var oldImage = req.Strategy.Equals("Canary", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(req.PreviousImageTag)
            ? $"{req.ImageRepository}:{req.PreviousImageTag}"
            : null;

        // ---- 2. Make the image available.
        await MaybeLoginAsync(req, ct);
        var dockerfile = string.IsNullOrWhiteSpace(req.DockerfilePath) ? "Dockerfile" : req.DockerfilePath;
        var dockerfileFull = Path.Combine(sourceDir, dockerfile);
        if (File.Exists(dockerfileFull))
        {
            var buildCtx = Path.Combine(sourceDir, string.IsNullOrWhiteSpace(req.DockerContext) ? "." : req.DockerContext);
            log.LogInformation("Building {Image} from {Dockerfile} (ctx {Ctx})", image, dockerfileFull, buildCtx);
            await BuildImageAsync(dockerfileFull, buildCtx, image, ct);
        }
        else
        {
            log.LogInformation("Pulling prebuilt {Image}", image);
            await RunAsync("docker", $"pull {image}", ct);
        }

        if (oldImage is not null)
        {
            log.LogInformation("Pulling previous {OldImage} for canary", oldImage);
            await RunAsync("docker", $"pull {oldImage}", ct);
        }

        // ---- 3. Render .env + docker-compose.yml (repo's own, or generated).
        var envContent = ComposeRenderer.RenderEnv(req.EnvVars);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".env"), envContent, ct);

        var composeFile = FindComposeFile(sourceDir);
        var hostPort = req.Domains?.FirstOrDefault(d => !d.IsStatic && d.TargetPort.HasValue)?.TargetPort ?? 8080;

        if (composeFile is null)
        {
            // No repo compose: generate one. Canary uses a blue/green pair of services.
            var generated = oldImage is null
                ? ComposeRenderer.RenderCompose(req, serviceName, hostPort)
                : ComposeRenderer.RenderCanaryCompose(req, serviceName, hostPort, image, oldImage);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "docker-compose.yml"), generated, ct);
        }

        // ---- 4. Render Caddyfile for domain routing / weighted canary.
        var caddyContent = ComposeRenderer.RenderCaddyfile(req, serviceName, hostPort);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Caddyfile"), caddyContent, ct);

        // ---- 5. Start the stack.
        log.LogInformation("compose -p {Slot} up -d (from {Dir})", req.Slot, sourceDir);
        await RunAsync("docker", $"compose -p {ComposeRenderer.Sanitize(req.Slot)} --env-file .env up -d", sourceDir, ct);

        // ---- 6. Health gate.
        var healthTarget = oldImage is null ? $"{serviceName}:{hostPort}" : $"{serviceName}-new:{hostPort}";
        var health = await WaitHealthyAsync(healthTarget, ct);

        var logFile = Path.Combine(sourceDir, "deploy.log");
        await File.WriteAllTextAsync(logFile, $"image={image}\nslot={req.Slot}\ncompose={(composeFile is null ? "generated" : composeFile)}\nhealth={health}\n", ct);

        return $"image={image}\nslot={req.Slot}\ncompose={(composeFile is null ? "generated" : "repo")}\nhealth={health}";
    }

    private async Task SourceFromGitAsync(AgentDeployRequest req, string sourceDir, CancellationToken ct)
    {
        var cloneUrl = req.GitRepository!;
        if (!string.IsNullOrWhiteSpace(req.GitToken))
        {
            // https://github.com/org/app -> https://<token>@github.com/org/app
            var uri = new Uri(cloneUrl);
            var host = uri.Host;
            var path = string.Join("/", uri.AbsolutePath.Trim('/').Split('/').Select(Uri.EscapeDataString));
            cloneUrl = $"https://x-access-token:{req.GitToken}@{host}/{path}";
        }

        var refsExist = Directory.Exists(sourceDir) && Directory.Exists(Path.Combine(sourceDir, ".git"));
        var branch = string.IsNullOrWhiteSpace(req.GitBranch) ? "main" : req.GitBranch;

        if (refsExist)
        {
            log.LogInformation("Fetching {Url} branch {Branch}", req.GitRepository, branch);
            await RunAsync("git", $"-C {sourceDir} fetch --depth 1 origin {branch}", ct);
            await RunAsync("git", $"-C {sourceDir} checkout {branch}", ct);
            await RunAsync("git", $"-C {sourceDir} pull --ff-only origin {branch}", ct);
        }
        else
        {
            log.LogInformation("Cloning {Url} branch {Branch}", req.GitRepository, branch);
            await RunAsync("git", $"clone --depth 1 --branch {branch} {cloneUrl} {sourceDir}", ct);
        }
    }

    private async Task MaybeLoginAsync(AgentDeployRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RegistryUsername) || string.IsNullOrWhiteSpace(req.RegistryPassword)) return;
        var registry = new Uri(req.ImageRepository).Host;
        log.LogInformation("docker login {Registry} as {User}", registry, req.RegistryUsername);
        await RunAsync("docker", $"login {registry} -u {req.RegistryUsername} --password-stdin", ct: ct, stdin: req.RegistryPassword);
    }

    private async Task BuildImageAsync(string dockerfileFull, string buildCtx, string image, CancellationToken ct)
    {
        var result = await RunAsync("docker", $"build -f {dockerfileFull} -t {image} {buildCtx}", ct);
        log.LogInformation("Build result: {Result}", result.Trim());
    }

    private string? FindComposeFile(string dir)
    {
        foreach (var name in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync()
    {
        using var client = Client;
        return await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
    }

    private async Task<string> RunAsync(string file, string args, string? workdir = null, CancellationToken ct = default, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        if (workdir is not null) psi.WorkingDirectory = workdir;
        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory());
            p.StandardInput.Close();
        }
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new Exception($"{file} {args} failed ({p.ExitCode}): {stderr}\n{stdout}");
        return stdout + stderr;
    }

    private async Task<string> WaitHealthyAsync(string target, CancellationToken ct)
    {
        foreach (var _ in Enumerable.Range(0, 12))
        {
            await Task.Delay(5000, ct);
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await http.GetAsync($"http://localhost:{target.Split(':')[1]}/health", ct);
                if (resp.IsSuccessStatusCode) return "healthy";
            }
            catch { }
        }
        return "unhealthy-timeout";
    }

    private Task<string> RunAsync(string file, string args, CancellationToken ct)
        => RunAsync(file, args, null, ct);
}
