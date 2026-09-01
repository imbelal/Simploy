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
        // The app's serving port. Multiple apps on one VM must use different ports.
        var hostPort = req.Domains?.FirstOrDefault(d => !d.IsStatic && d.TargetPort.HasValue)?.TargetPort ?? 8080;

        // ---- 2. Make the image available.
        await MaybeLoginAsync(req, ct);
        var dockerfile = string.IsNullOrWhiteSpace(req.DockerfilePath) ? "Dockerfile" : req.DockerfilePath;
        var dockerfileFull = Path.Combine(sourceDir, dockerfile);
        if (!File.Exists(dockerfileFull) && File.Exists(Path.Combine(sourceDir, "package.json")))
        {
            // Static/frontend app with no Dockerfile: generate one (Node build ->
            // nginx on hostPort with a /health endpoint), so it can be deployed as-is.
            log.LogInformation("No Dockerfile found; generating a static-site Dockerfile for {Repo}", req.GitRepository);
            await WriteStaticDockerfileAsync(sourceDir, hostPort, ct);
            dockerfileFull = Path.Combine(sourceDir, "Dockerfile");
        }

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
        // Target the actual compose service that hosts this app (e.g. 'webapi'),
        // not the symbolic slug name.
        var appService = composeFile is null ? serviceName : (FindAppService(composeContent, req.ImageRepository) ?? serviceName);
        var caddyContent = ComposeRenderer.RenderCaddyfile(req, appService, hostPort);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Caddyfile"), caddyContent, ct);

        // Compose files can declare 'external: true' networks that must already
        // exist (e.g. bd-shop-manager-network). Create any missing ones first.
        await EnsureExternalNetworksAsync(composeContent, ct);

        // SQL Server 2022 runs as non-root and can't write its host bind-mount data
        // dir (Access is denied). Run mssql services as root via a Simploy compose
        // override so its setup works on a fresh VM without editing the app repo.
        var mssqlServices = GetMssqlServices(composeContent);
        if (mssqlServices.Count > 0)
        {
            var overrrideYaml = "services:\n" + string.Concat(mssqlServices.Select(s => $"  {s}:\n    user: root\n"));
            await File.WriteAllTextAsync(Path.Combine(sourceDir, ".simploy-compose.override.yml"), overrrideYaml, ct);
            log.LogInformation("Running mssql service(s) as root: {Services}", string.Join(", ", mssqlServices));
        }

        // ---- 5. Start the stack.
        var overrideFile = Path.Combine(sourceDir, ".simploy-compose.override.yml");
        var baseCompose = composeFile is null ? "docker-compose.yml" : Path.GetFileName(composeFile);
        var composeCmd = File.Exists(overrideFile)
            ? $"compose -f {baseCompose} -f .simploy-compose.override.yml -p {ComposeRenderer.Sanitize(req.Slot)} --env-file .env up -d"
            : $"compose -p {ComposeRenderer.Sanitize(req.Slot)} --env-file .env up -d";
        log.LogInformation("compose -p {Slot} up -d (from {Dir})", req.Slot, sourceDir);
        await RunAsync("docker", composeCmd, sourceDir, ct);

        // Caddy mounts the generated Caddyfile; reload it so new domains apply
        // without waiting for a container recreate.
        foreach (var svc in GetCaddyServices(composeContent))
        {
            log.LogInformation("Reloading {Svc} to pick up new Caddyfile", svc);
            try { await RunAsync("docker", $"compose -p {ComposeRenderer.Sanitize(req.Slot)} restart {svc}", sourceDir, ct); }
            catch (Exception ex) { log.LogWarning("Could not reload {Svc}: {Ex}", svc, ex.Message); }
        }

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

    /// <summary>Writes a Dockerfile + nginx conf for a static frontend app that ships no
    /// Dockerfile (e.g. a React build). Serves the built dist on the given port with a /health.</summary>
    private static async Task WriteStaticDockerfileAsync(string sourceDir, int port, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "simploy-nginx.conf"), $"""
        server {{
            listen {port};
            server_name _;
            root /usr/share/nginx/html;
            index index.html;
            location / {{ try_files $uri /index.html; }}
            location /health {{ return 200 "ok"; }}
        }}
        """, ct);

        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Dockerfile"), """
        FROM node:20-alpine AS build
        WORKDIR /app
        COPY package*.json ./
        RUN npm ci || npm install
        COPY . .
        RUN npm run build

        FROM nginx:alpine
        COPY --from=build /app/dist /usr/share/nginx/html
        COPY simploy-nginx.conf /etc/nginx/conf.d/default.conf
        EXPOSE 8080
        """, ct);
    }

    private async Task MaybeLoginAsync(AgentDeployRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RegistryUsername) || string.IsNullOrWhiteSpace(req.RegistryPassword)) return;

        // Derive the registry host from the image repo. Only docker login when there is
        // a real registry (host[:port]); a bare name (e.g. the project slug, or Docker
        // Hub "user/repo") has no host and doesn't need a login for source-builds.
        var repo = req.ImageRepository ?? "";
        if (repo.Contains("://")) repo = repo[(repo.IndexOf("://") + 3)..];
        var parts = repo.Split('/');
        var registry = parts[0];
        if (parts.Length < 2 || (!registry.Contains('.') && !registry.Contains(':')))
        {
            log.LogWarning("No registry host detected in image repo {Repo}; skipping docker login", repo);
            return;
        }

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

    /// <summary>Returns the service names whose image is SQL Server (run as root).</summary>
    /// <summary>Returns service names whose image contains 'caddy' (the reverse proxy).</summary>
    private static List<string> GetCaddyServices(string composeContent)
    {
        var result = new List<string>();
        var inServices = false;
        string? current = null;
        foreach (var raw in composeContent.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();
            if (!inServices) { if (indent == 0 && t.StartsWith("services:")) inServices = true; continue; }
            if (indent == 0) break;
            if (indent == 2 && t.EndsWith(":")) { current = t.TrimEnd(':'); continue; }
            if (indent == 4 && t.StartsWith("image:") && current is not null && t.Contains("caddy", StringComparison.OrdinalIgnoreCase))
            { if (!result.Contains(current)) result.Add(current); }
        }
        return result;
    }

    /// <summary>Returns the service name whose image matches the app's image repo.</summary>
    private static string? FindAppService(string composeContent, string imageRepository)
    {
        var repoBase = imageRepository;
        if (repoBase.Contains("://")) repoBase = repoBase[(repoBase.IndexOf("://") + 3)..];

        var inServices = false;
        string? current = null;
        foreach (var raw in composeContent.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();

            if (!inServices) { if (indent == 0 && t.StartsWith("services:")) inServices = true; continue; }
            if (indent == 0) break;
            if (indent == 2 && t.EndsWith(":")) { current = t.TrimEnd(':'); continue; }
            if (indent == 4 && t.StartsWith("image:") && current is not null && t.Contains(repoBase, StringComparison.OrdinalIgnoreCase))
                return current;
        }
        return null;
    }

    private static List<string> GetMssqlServices(string composeContent)
    {
        var result = new List<string>();
        var inServices = false;
        string? current = null;
        foreach (var raw in composeContent.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();

            if (!inServices) { if (indent == 0 && t.StartsWith("services:")) inServices = true; continue; }
            if (indent == 0) break;

            if (indent == 2 && t.EndsWith(":")) { current = t.TrimEnd(':'); continue; }
            if (indent == 4 && t.StartsWith("image:"))
            {
                var img = t;
                if (img.Contains("mssql", StringComparison.OrdinalIgnoreCase)
                    || img.Contains("sqlserver", StringComparison.OrdinalIgnoreCase)
                    || img.Contains("sql-server", StringComparison.OrdinalIgnoreCase))
                { if (current is not null && !result.Contains(current)) result.Add(current); }
            }
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
