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
public class DockerService(IConfiguration config, ILogger<DockerService> log)
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
        // Write .env FIRST so it's in the build context: Vite reads VITE_* vars from
        // .env at build time (and docker compose uses it as env_file at runtime).
        var envContent = ComposeRenderer.RenderEnv(req.EnvVars);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".env"), envContent, ct);

        await MaybeLoginAsync(req, ct);
        var dockerfile = string.IsNullOrWhiteSpace(req.DockerfilePath) ? "Dockerfile" : req.DockerfilePath;
        var dockerfileFull = Path.Combine(sourceDir, dockerfile);
        // If the repo does NOT track a Dockerfile but has package.json, it's a static
        // frontend app: generate (or regenerate) one so current VITE_* vars are baked.
        if (!await IsGitTrackedAsync(sourceDir, dockerfile, ct) && File.Exists(Path.Combine(sourceDir, "package.json")))
        {
            // Static/frontend app with no Dockerfile (or a stale Simploy-generated one):
            // generate one (Node build -> nginx on hostPort with a /health endpoint).
            log.LogInformation("Generating a static-site Dockerfile for {Repo}", req.GitRepository);
            await WriteStaticDockerfileAsync(sourceDir, hostPort, req.EnvVars, ct);
            dockerfileFull = Path.Combine(sourceDir, "Dockerfile");
        }

        if (File.Exists(dockerfileFull))
        {
            var buildCtx = Path.Combine(sourceDir, string.IsNullOrWhiteSpace(req.DockerContext) ? "." : req.DockerContext);
            log.LogInformation("Building {Image} from {Dockerfile} (ctx {Ctx})", image, dockerfileFull, buildCtx);
            // VITE_* vars go in as build args -> process.env, which Vite treats as the
            // highest priority (beats committed .env.production etc.).
            var buildArgs = req.EnvVars?.Where(kv => kv.Key.StartsWith("VITE_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            await BuildImageAsync(dockerfileFull, buildCtx, image, ct, buildArgs);
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

        // ---- 3. Render docker-compose.yml (repo's own, or generated).
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

        // Shared proxy: generated composes (and anything joining 'simploy-proxy') are
        // routed by the Simploy-managed Caddy. Ensure the network + Caddy exist and
        // register this app's domains as a fragment (picked up via Caddy 'import').
        await EnsureSharedProxyAsync(ct);
        if (composeFile is null || composeContent.Contains("simploy-proxy", StringComparison.Ordinal))
            await WriteProxyFragmentAsync(req, appService, hostPort, composeContent, ct);

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

        // Reload the shared proxy so this app's domains go live.
        await ReloadSharedProxyAsync(ct);

        // ---- 6. Health gate (no host port published, so check the container's status).
        var healthService = oldImage is null ? appService : $"{appService}-new";
        var health = await WaitHealthyAsync(req.Slot, healthService, sourceDir, ct);

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

    // ===== Managed databases =====
    public async Task<string> ProvisionDatabaseAsync(AgentDbRequest req, CancellationToken ct)
    {
        var dbName = ComposeRenderer.Sanitize(req.DbName);
        var dir = Path.Combine(BaseDir, ".databases", dbName);
        Directory.CreateDirectory(dir);

        var (image, volumePath, healthcheck) = DbSpec(req);
        var envVars = DbEnv(req, out var dataVolume);
        await File.WriteAllTextAsync(Path.Combine(dir, ".env"), ComposeRenderer.RenderEnv(envVars), ct);

        var compose = new StringBuilder();
        compose.AppendLine("services:");
        compose.AppendLine($"  {dbName}:");
        compose.AppendLine($"    container_name: db-{dbName}");
        compose.AppendLine($"    image: \"{image}\"");
        if (req.Type.Equals("redis", StringComparison.OrdinalIgnoreCase))
            compose.AppendLine("    command: [\"sh\",\"-c\",\"exec redis-server --requirepass \\\"$REDIS_PASSWORD\\\"\"]");
        compose.AppendLine("    env_file: .env");
        compose.AppendLine("    restart: unless-stopped");
        // SQL Server 2022 runs as non-root and can't write its data dir; run as root so it self-manages.
        if (req.Type.Equals("mssql", StringComparison.OrdinalIgnoreCase))
            compose.AppendLine("    user: root");
        compose.AppendLine("    volumes:");

        // Bind mount to a host dir (writable) if requested, else a Docker named volume.
        if (!string.IsNullOrWhiteSpace(req.DataPath))
        {
            var hostDir = req.DataPath!.Trim();
            await RunAsync("sh", $"-c \"mkdir -p {hostDir} && chmod -R 0777 {hostDir}\"", ct);
            compose.AppendLine($"      - {hostDir}:{volumePath}");
            dataVolume = "";
        }
        else
        {
            dataVolume = $"{dbName}-data";
            compose.AppendLine($"      - {dataVolume}:{volumePath}");
        }

        compose.AppendLine("    healthcheck:");
        // YAML-escape the healthcheck shell command (it may contain double quotes).
        var escaped = healthcheck.Replace("\\", "\\\\").Replace("\"", "\\\"");
        compose.AppendLine($"      test: [\"CMD-SHELL\", \"{escaped}\"]");
        compose.AppendLine("      interval: 5s");
        compose.AppendLine("      timeout: 5s");
        compose.AppendLine("      retries: 20");
        compose.AppendLine("    networks:");
        compose.AppendLine("      - simploy-proxy");
        if (!string.IsNullOrEmpty(dataVolume))
        {
            compose.AppendLine("volumes:");
            compose.AppendLine($"  {dataVolume}:");
        }
        compose.AppendLine("networks:");
        compose.AppendLine("  simploy-proxy:");
        compose.AppendLine("    external: true");
        await File.WriteAllTextAsync(Path.Combine(dir, "docker-compose.yml"), compose.ToString(), ct);

        await EnsureSharedProxyAsync(ct);
        await RunAsync("docker", $"compose -p db-{dbName} --env-file .env up -d", dir, ct);
        var health = await WaitHealthyAsync($"db-{dbName}", dbName, dir, ct);
        return $"provisioned db-{dbName} health={health}";
    }

    public async Task<string> RemoveDatabaseAsync(AgentDbRequest req, CancellationToken ct)
    {
        var dbName = ComposeRenderer.Sanitize(req.DbName);
        var dir = Path.Combine(BaseDir, ".databases", dbName);
        await RunAsync("docker", $"compose -p db-{dbName} down -v", dir, ct);
        return $"removed db-{dbName}";
    }

    private static (string image, string volumePath, string healthcheck) DbSpec(AgentDbRequest req) => (req.Type.ToLowerInvariant(), req.Version) switch
    {
        ("mysql", var v) => ($"mysql:{v}", "/var/lib/mysql", "mysqladmin ping -h localhost -u root -p\"$MYSQL_ROOT_PASSWORD\" || exit 1"),
        ("redis", var v) => ($"redis:{v}-alpine", "/data", "redis-cli -a \"$REDIS_PASSWORD\" ping | grep PONG"),
        ("mongodb", var v) => ($"mongo:{v}", "/data/db", "mongosh --quiet --eval \"db.runCommand('ping').ok\" --username $MONGO_INITDB_ROOT_USERNAME --password $MONGO_INITDB_ROOT_PASSWORD admin | grep 1"),
        ("mssql", var v) => ($"mcr.microsoft.com/mssql/server:{v}-latest", "/var/opt/mssql", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$MSSQL_SA_PASSWORD\" -Q 'SELECT 1' -No || exit 1"),
        _ => ($"postgres:{req.Version}-alpine", "/var/lib/postgresql/data", "pg_isready -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\""),
    };

    private static Dictionary<string, string> DbEnv(AgentDbRequest req, out string dataVolume)
    {
        dataVolume = $"{ComposeRenderer.Sanitize(req.DbName)}-data";
        return req.Type.ToLowerInvariant() switch
        {
            "mysql" => new() {
                ["MYSQL_DATABASE"] = req.DatabaseName,
                ["MYSQL_USER"] = req.Username,
                ["MYSQL_PASSWORD"] = req.Password,
                ["MYSQL_ROOT_PASSWORD"] = req.Password,
            },
            "redis" => new() { ["REDIS_PASSWORD"] = req.Password },
            "mongodb" => new() {
                ["MONGO_INITDB_ROOT_USERNAME"] = req.Username,
                ["MONGO_INITDB_ROOT_PASSWORD"] = req.Password,
            },
            "mssql" => new() {
                ["ACCEPT_EULA"] = "Y",
                ["MSSQL_SA_PASSWORD"] = req.Password,
                ["MSSQL_PID"] = "Express",
            },
            _ => new() {
                ["POSTGRES_DB"] = req.DatabaseName,
                ["POSTGRES_USER"] = req.Username,
                ["POSTGRES_PASSWORD"] = req.Password,
            },
        };
    }

    // ===== Control-plane backup (pg_dump via docker exec) =====
    public async Task<string> RunBackupAsync(AgentBackupRequest req, CancellationToken ct)
    {
        Directory.CreateDirectory(req.DestDir);
        var file = Path.Combine(req.DestDir, $"simploy_{DateTime.UtcNow:yyyyMMdd_HHmmss}.sql");
        var output = await RunStdoutOnlyAsync("docker",
            $"exec -e PGPASSWORD={req.Password} {req.Container} pg_dump -U {req.Username} -d {req.DatabaseName}", ct);
        await File.WriteAllTextAsync(file, output, ct);
        PruneBackups(req.DestDir, req.Retention);
        return $"backup={file} bytes={new FileInfo(file).Length}";
    }

    public async Task<string> RestoreBackupAsync(AgentBackupRequest req, CancellationToken ct)
    {
        var file = req.DestDir; // DestDir carries the file path here
        var sql = await File.ReadAllTextAsync(file, ct);
        await RunAsync("docker",
            $"exec -i {req.Container} psql -U {req.Username} -d {req.DatabaseName}", ct: ct, stdin: sql);
        return $"restored from {file}";
    }

    public List<object> ListBackups(string dir)
    {
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).GetFiles("*.sql")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => (object)new { file = f.FullName, name = f.Name, size = f.Length, created = f.LastWriteTime })
            .ToList();
    }

    private static void PruneBackups(string dir, int keep)
    {
        if (keep <= 0) return;
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.sql").OrderByDescending(f => f.LastWriteTime).Skip(keep))
            f.Delete();
    }

    /// <summary>Runs a command capturing only stdout (clean output, e.g. pg_dump SQL).</summary>
    private async Task<string> RunStdoutOnlyAsync(string file, string args, CancellationToken ct = default, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new Exception($"{file} {args} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    /// <summary>Returns true if the given path is tracked by the repo's git (i.e. ships with
    /// the app). Untracked files are Simploy-generated leftovers.</summary>
    private async Task<bool> IsGitTrackedAsync(string sourceDir, string relativePath, CancellationToken ct)
    {
        try { await RunAsync("git", $"-C {sourceDir} ls-files --error-unmatch {relativePath}", ct); return true; }
        catch { return false; }
    }

    /// <summary>Writes a Dockerfile + nginx conf for a static frontend app that ships no
    /// Dockerfile (e.g. a React build). Serves the built dist on the given port with a /health.
    /// VITE_* env vars are declared as ARG/ENV so they're baked at build time.</summary>
    private static async Task WriteStaticDockerfileAsync(string sourceDir, int port, IReadOnlyDictionary<string, string>? envs, CancellationToken ct)
    {
        const string nginxConf = """
        server {
            listen __PORT__;
            server_name _;
            root /usr/share/nginx/html;
            index index.html;
            location / { try_files $uri /index.html; }
            location /health { return 200 "ok"; }
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "simploy-nginx.conf"),
            nginxConf.Replace("__PORT__", port.ToString()), ct);

        var args = new List<string>
        {
            "# Generated by Simploy",
            "FROM node:20-alpine AS build",
            "WORKDIR /app",
            "COPY package*.json ./",
            "RUN npm ci || npm install",
            "COPY . .",
        };

        // VITE_* as ARG/ENV -> process.env, which Vite scores highest (beats .env*).
        foreach (var kv in envs ?? new Dictionary<string, string>())
            if (kv.Key.StartsWith("VITE_", StringComparison.OrdinalIgnoreCase))
            {
                args.Add($"ARG {kv.Key}");
                args.Add($"ENV {kv.Key}=${kv.Key}");
            }

        args.AddRange(new[]
        {
            "RUN npm run build",
            "",
            "FROM nginx:alpine",
            "COPY --from=build /app/dist /usr/share/nginx/html",
            "COPY simploy-nginx.conf /etc/nginx/conf.d/default.conf",
            $"EXPOSE {port}",
        });

        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Dockerfile"), string.Join("\n", args), ct);
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

    private async Task BuildImageAsync(string dockerfileFull, string buildCtx, string image, CancellationToken ct, IReadOnlyDictionary<string, string>? buildArgs = null)
    {
        // Force BuildKit so Dockerfiles using --mount / cache work (needs buildx).
        var env = new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1", ["BUILDKIT_PROGRESS"] = "plain" };
        var args = string.Concat((buildArgs ?? new Dictionary<string, string>()).Select(kv => $" --build-arg {kv.Key}={kv.Value}"));
        var result = await RunAsync("docker", $"build -f {dockerfileFull} -t {image} {buildCtx}{args}", ct: ct, env: env);
        log.LogInformation("Build result: {Result}", result.Trim());
    }

    /// <summary>Returns the app's own compose file. Files generated by Simploy (marked with
    /// a 'simploy.project' label) are ignored so a stale generated compose from a previous
    /// deploy isn't mistaken for the app's own.</summary>
    private string? FindComposeFile(string dir)
    {
        foreach (var name in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            var content = File.ReadAllText(p);
            if (content.Contains("simploy.project:", StringComparison.Ordinal)) continue; // Simploy-generated
            return p;
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

    private const string ProxyNetwork = "simploy-proxy";
    private const string ProxyCaddy = "simploy-caddy";
    private static readonly string ProxyDir = Path.Combine(BaseDir, ".proxy");
    private static readonly string ProxyAppsDir = Path.Combine(BaseDir, ".proxy", "apps");

    public Task EnsureProxyAsync(CancellationToken ct) => EnsureSharedProxyAsync(ct);

    /// <summary>Creates the shared proxy network, base Caddyfile and Caddy container so all
    /// Simploy-generated apps are routed by domain through one proxy.</summary>
    private async Task EnsureSharedProxyAsync(CancellationToken ct)
    {
        try { await RunAsync("docker", $"network inspect {ProxyNetwork}", ct); }
        catch { log.LogInformation("Creating shared proxy network {Network}", ProxyNetwork); await RunAsync("docker", $"network create {ProxyNetwork}", ct); }

        Directory.CreateDirectory(ProxyAppsDir);
        await File.WriteAllTextAsync(Path.Combine(ProxyDir, "Caddyfile"), "import /etc/caddy/apps/*.conf\n", ct);

        // Serve the Simploy control plane on its own domain (if configured) via the proxy.
        var controlDomain = config["ControlPlane:Domain"];
        if (!string.IsNullOrWhiteSpace(controlDomain))
        {
            var controlConf = $"{controlDomain.Trim()} {{\n    reverse_proxy simploy-control:80\n}}\n";
            await File.WriteAllTextAsync(Path.Combine(ProxyAppsDir, "simploy-control.conf"), controlConf, ct);
            log.LogInformation("Control plane domain: {Domain}", controlDomain);
        }

        try { await RunAsync("docker", $"inspect {ProxyCaddy}", ct); return; } // already running
        catch { /* start it */ }

        log.LogInformation("Starting shared proxy Caddy ({Name})", ProxyCaddy);
        try
        {
            await RunAsync("docker",
                $"run -d --name {ProxyCaddy} --restart unless-stopped --network {ProxyNetwork} " +
                $"-p 80:80 -p 443:443 " +
                $"-v {ProxyDir}/Caddyfile:/etc/caddy/Caddyfile " +
                $"-v {ProxyAppsDir}:/etc/caddy/apps " +
                $"caddy:latest", ct);
        }
        catch (Exception ex)
        {
            // Ports 80/443 may be held by an app that ships its own Caddy. The app still
            // deploys; routing just needs the other proxy removed first.
            log.LogWarning("Could not start shared proxy Caddy: {Ex}", ex.Message);
        }
    }

    /// <summary>Writes this app's domain fragment into the shared proxy's config dir.
    /// The service is auto-matched from the domain name (e.g. 'staging-seq' -> 'seq'),
    /// and the port is auto-detected from the compose. Just add the domain.</summary>
    private async Task WriteProxyFragmentAsync(AgentDeployRequest req, string appService, int port, string composeContent, CancellationToken ct)
    {
        if (req.Domains is null || req.Domains.Count == 0) return;
        Directory.CreateDirectory(ProxyAppsDir);
        var services = GetServices(composeContent);
        var sb = new StringBuilder();
        foreach (var d in req.Domains)
        {
            if (d.IsStatic || string.IsNullOrWhiteSpace(d.Host)) continue;
            sb.AppendLine(d.EnableHttps ? $"{d.Host} {{" : $"http://{d.Host} {{");

            // Service: pinned target > auto-matched from host labels > the app's main service.
            var svc = d.TargetService?.Split(':')[0].Trim();
            var pinPort = d.TargetService?.Contains(':') == true ? d.TargetService!.Split(':')[1].Trim() : null;
            svc = !string.IsNullOrWhiteSpace(svc) ? svc : (MatchService(d.Host, services) ?? appService);
            var p = pinPort ?? (d.TargetPort ?? GetServiceInternalPort(composeContent, svc) ?? 8080).ToString();
            sb.AppendLine($"    reverse_proxy {svc}:{p}");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        var file = Path.Combine(ProxyAppsDir, $"{ComposeRenderer.Sanitize(req.ProjectSlug)}-{ComposeRenderer.Sanitize(req.Slot)}.conf");
        await File.WriteAllTextAsync(file, sb.ToString(), ct);
        log.LogInformation("Wrote proxy fragment {File}", file);
    }

    /// <summary>Finds a compose service whose name appears as a label of the domain host
    /// (e.g. 'staging-seq.imbelal.com' -> 'seq'), else null.</summary>
    private static string? MatchService(string host, List<string> services)
    {
        var labels = host.ToLowerInvariant().Split('.');
        foreach (var svc in services)
            if (labels.Contains(svc.ToLowerInvariant())) return svc;
        return null;
    }

    /// <summary>Lists the top-level service names in a compose.</summary>
    private static List<string> GetServices(string? composeContent)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(composeContent)) return result;
        var inServices = false;
        foreach (var raw in composeContent.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var t = raw.Trim();
            if (!inServices) { if (indent == 0 && t.StartsWith("services:")) inServices = true; continue; }
            if (indent == 0) break;
            if (indent == 2 && t.EndsWith(":")) { var name = t.TrimEnd(':'); if (!result.Contains(name)) result.Add(name); }
        }
        return result;
    }

    /// <summary>Best-effort parse of a service's internal (container) port from the compose.
    /// Uses the right-hand side of the first "ports:" mapping, e.g. "127.0.0.1:5341:80" -> 80.</summary>
    private static int? GetServiceInternalPort(string? composeContent, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(composeContent)) return null;
        var lines = composeContent.Split('\n');
        var inService = false;
        var inPorts = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();

            if (!inService)
            {
                if (indent == 2 && t.StartsWith($"{serviceName}:")) inService = true;
                continue;
            }
            if (indent < 2) break; // left the service

            if (indent == 4 && t.StartsWith("ports:")) { inPorts = true; continue; }
            if (inPorts && indent == 6 && t.StartsWith("-"))
            {
                var vals = t.TrimStart('-').Trim().Split(':').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                if (vals.Length >= 1 && int.TryParse(vals[^1], out var p)) return p;
            }
            if (indent < 4) inPorts = false;
        }
        return null;
    }

    // Restart reloads Caddy so newly written import fragments (domains) are always picked up
    // (caddy reload does not reliably re-import new files in the apps dir).
    private async Task ReloadSharedProxyAsync(CancellationToken ct)
    {
        await RunAsync("docker", $"restart {ProxyCaddy}", ct);
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
            DeployLog.Write(line);
        }
    }

    /// <summary>Waits for the app container to be running (no host port is published, so
    /// we check the container's Docker health / status instead of an HTTP endpoint).</summary>
    private async Task<string> WaitHealthyAsync(string slot, string service, string workdir, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(3000, ct);
            try
            {
                var cid = (await RunAsync("docker", $"compose -p {ComposeRenderer.Sanitize(slot)} ps -q {service}", workdir, ct)).Trim();
                if (string.IsNullOrEmpty(cid)) continue; // not created/up yet

                var status = (await RunAsync("docker", $"inspect {cid} --format {{{{.State.Status}}}}", ct)).Trim();
                if (status == "running") return "healthy";
                if (status is "exited" or "dead" or "restarting") return $"unhealthy({status})";
            }
            catch { }
        }
        return "unhealthy-timeout";
    }

    private Task<string> RunAsync(string file, string args, CancellationToken ct)
        => RunAsync(file, args, null, ct);
}
