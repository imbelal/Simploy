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

        // Many shipped docker-compose.yml files reference the app image as :latest.
        // Tag the freshly built/pulled image as latest so compose resolves it locally
        // instead of trying (and failing) to pull the tag from the registry.
        await RunAsync("docker", $"tag {image} {req.ImageRepository}:latest", ct);

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

        string composeContent;
        if (composeFile is null)
        {
            // No repo compose: generate one. Canary uses a blue/green pair of services.
            composeContent = oldImage is null
                ? ComposeRenderer.RenderCompose(req, serviceName, hostPort)
                : ComposeRenderer.RenderCanaryCompose(req, serviceName, hostPort, image, oldImage);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "docker-compose.yml"), composeContent, ct);
        }
        else
        {
            composeContent = await File.ReadAllTextAsync(composeFile, ct);
        }

        // ---- 4. Render Caddyfile for domain routing / weighted canary.
        var caddyContent = ComposeRenderer.RenderCaddyfile(req, serviceName, hostPort);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Caddyfile"), caddyContent, ct);

        // Compose files can declare 'external: true' networks that must already
        // exist (e.g. bd-shop-manager-network). Create any missing ones first.
        await EnsureExternalNetworksAsync(composeContent, ct);

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
        // Allow "github.com/org/app" as well as "https://github.com/org/app".
        var repo = req.GitRepository!.Trim();
        if (!repo.Contains("://")) repo = $"https://{repo}";
        var cloneUrl = repo;
        if (!string.IsNullOrWhiteSpace(req.GitToken))
        {
            // https://github.com/org/app -> https://x-access-token:<token>@github.com/org/app
            var uri = new Uri(cloneUrl);
            var host = uri.Host;
            var path = string.Join("/", uri.AbsolutePath.Trim('/').Split('/').Select(Uri.EscapeDataString));
            cloneUrl = $"https://x-access-token:{req.GitToken}@{host}/{path}";
        }

        var refsExist = Directory.Exists(sourceDir) && Directory.Exists(Path.Combine(sourceDir, ".git"));
        var branch = string.IsNullOrWhiteSpace(req.GitBranch) ? "main" : req.GitBranch;

        try
        {
            if (!refsExist)
            {
                log.LogInformation("Cloning {Repo}", req.GitRepository);
                await RunAsync("git", $"clone --depth 1 {cloneUrl} {sourceDir}", ct);
            }

            // Try to move to the configured branch; if it doesn't exist (e.g. the repo
            // default is 'master', not 'main'), fall back to the remote default branch.
            string target;
            try
            {
                await RunAsync("git", $"-C {sourceDir} fetch --depth 1 origin {branch}", ct);
                target = branch;
            }
            catch
            {
                log.LogWarning("Branch {Branch} not found on {Repo}; using repo default branch", branch, req.GitRepository);
                await RunAsync("git", $"-C {sourceDir} fetch --depth 1 origin", ct);
                var head = await RunAsync("git", $"-C {sourceDir} symbolic-ref --short refs/remotes/origin/HEAD", ct);
                target = head.Trim().Replace("origin/", "");
            }
            await RunAsync("git", $"-C {sourceDir} reset --hard origin/{target}", ct);
            await RunAsync("git", $"-C {sourceDir} checkout -B {target} origin/{target}", ct);
        }
        catch (Exception ex)
        {
            throw Redact(ex, req.GitToken);
        }
    }

    /// <summary>Strips credentials from a command error before it is surfaced, so a PAT
    /// embedded in a clone URL is never written into the deployment log.</summary>
    private static Exception Redact(Exception ex, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return ex;
        var msg = ex.Message;
        var changed = false;
        var encoded = Uri.EscapeDataString(secret);
        if (msg.Contains(encoded)) { msg = msg.Replace(encoded, "***"); changed = true; }
        if (msg.Contains(secret)) { msg = msg.Replace(secret, "***"); changed = true; }
        return changed ? new Exception(msg, ex.InnerException) : ex;
    }

    private async Task MaybeLoginAsync(AgentDeployRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RegistryUsername) || string.IsNullOrWhiteSpace(req.RegistryPassword)) return;

        // Derive the registry host without URI parsing: ImageRepository may be
        // "ghcr.io/imbelal/app" (no scheme) or "https://ghcr.io/imbelal/app".
        var repo = req.ImageRepository ?? "";
        if (repo.Contains("://")) repo = repo[(repo.IndexOf("://") + 3)..];
        var registry = repo.Split('/')[0];
        if (string.IsNullOrWhiteSpace(registry)) return;

        log.LogInformation("docker login {Registry} as {User}", registry, req.RegistryUsername);
        await RunAsync("docker", $"login {registry} -u {req.RegistryUsername} --password-stdin", ct: ct, stdin: req.RegistryPassword);
    }

    private async Task BuildImageAsync(string dockerfileFull, string buildCtx, string image, CancellationToken ct)
    {
        // Force BuildKit so Dockerfiles using --mount / cache work (needs buildx).
        var env = new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1", ["BUILDKIT_PROGRESS"] = "plain" };
        var result = await RunAsync("docker", $"build -f {dockerfileFull} -t {image} {buildCtx}", ct: ct, env: env);
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

    /// <summary>Creates networks declared as external in the compose file (e.g.
    /// bd-shop-manager-network), which docker compose requires to already exist.</summary>
    private async Task EnsureExternalNetworksAsync(string composeContent, CancellationToken ct)
    {
        var networks = GetExternalNetworks(composeContent);
        log.LogInformation("External networks in compose: [{Networks}]", string.Join(", ", networks));
        foreach (var network in networks)
        {
            try { await RunAsync("docker", $"network inspect {network}", ct); continue; } // exists
            catch { /* missing */ }
            log.LogInformation("Creating external network {Network}", network);
            await RunAsync("docker", $"network create {network}", ct);
        }
    }

    private static List<string> GetExternalNetworks(string composeContent)
    {
        var result = new List<string>();
        var inNetworks = false;
        string? current = null;
        foreach (var raw in composeContent.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();

            // Only the top-level (indent 0) 'networks:' block qualifies; service-level
            // 'networks:' lists (indented) must be ignored.
            if (!inNetworks) { if (indent == 0 && t.StartsWith("networks:")) inNetworks = true; continue; }
            if (indent == 0) break; // back to another top-level key -> end of networks block

            if (indent == 2 && t.EndsWith(":")) { current = t.TrimEnd(':'); continue; }
            if (indent == 4 && t.StartsWith("external:") && t.TrimEnd().EndsWith("true") && current is not null && !result.Contains(current))
                result.Add(current);
        }
        return result;
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync()
    {
        using var client = Client;
        return await client.Containers.ListContainersAsync(new ContainersListParameters { All = true });
    }

    private async Task<string> RunAsync(string file, string args, string? workdir = null, CancellationToken ct = default, string? stdin = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        if (workdir is not null) psi.WorkingDirectory = workdir;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory());
            p.StandardInput.Close();
        }
        var sb = new StringBuilder();
        var stdoutTask = ReadLinesAsync(p.StandardOutput, sb, ct);
        var stderrTask = ReadLinesAsync(p.StandardError, sb, ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new Exception($"{file} {args} failed ({p.ExitCode}): {sb}");
        return sb.ToString();
    }

    // Reads a command stream line-by-line so build output is streamed live to the
    // agent logs (visible via `docker logs -f`) while still being captured.
    private async Task ReadLinesAsync(StreamReader reader, StringBuilder sb, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            sb.AppendLine(line);
            log.LogInformation("  {Line}", line);
        }
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
