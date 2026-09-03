using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Simploy.Api.Data;
using Simploy.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    o.JsonSerializerOptions.WriteIndented = false;
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => { try { var u = new Uri(origin); return u.Host == "localhost" || u.Host == "127.0.0.1"; } catch { return false; } })
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var useInMemory = builder.Configuration.GetValue<bool>("UseInMemoryDb");
if (useInMemory)
    builder.Services.AddDbContext<SimployDbContext>(o => o.UseInMemoryDatabase("simploy"));
else
    builder.Services.AddDbContext<SimployDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddSingleton<GitHubAppService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.AddHostedService<DeploymentPoller>();
builder.Services.AddHostedService<DatabaseWorker>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<UptimeWorker>();

// ---- JWT auth (single admin user from config) ----
var jwtSecret = builder.Configuration["Auth:JwtSecret"] ?? "simploy-dev-secret-change-me";
var jwtIssuer = builder.Configuration["Auth:JwtIssuer"] ?? "simploy";
var jwtAudience = builder.Configuration["Auth:JwtAudience"] ?? "simploy";
builder.Services.AddSingleton<AuthService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwtIssuer,
        ValidateAudience = true, ValidAudience = jwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    // Auto-repair: if the postgres role password has drifted from the connection string
    // (28P01), reset it in place via the Agent's docker socket before ensuring the schema.
    RepairPostgresAuth(db, builder.Configuration, log);

    db.Database.EnsureCreated();
    // EnsureCreated does not alter an existing database, so apply lightweight
    // additive schema changes here (idempotent, Postgres only).
    if (db.Database.IsRelational())
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Domains\" ADD COLUMN IF NOT EXISTS \"EnableHttps\" boolean NOT NULL DEFAULT false; ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"GithubInstallationId\" text NULL; ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"Template\" text NULL; ALTER TABLE \"Deployments\" ADD COLUMN IF NOT EXISTS \"AgentJobId\" text NULL; ALTER TABLE \"Environments\" ADD COLUMN IF NOT EXISTS \"AutoRollback\" boolean NOT NULL DEFAULT true;");
        // Databases table (added after EnsureCreated for an existing DB).
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Databases" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Name" text NOT NULL,
                "Type" text NOT NULL,
                "Version" text NOT NULL,
                "ServerId" uuid NULL,
                "Slot" text NOT NULL,
                "Username" text NOT NULL,
                "Password" text NOT NULL,
                "DatabaseName" text NOT NULL,
                "Port" integer NOT NULL,
                "Status" text NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_Databases_Servers_ServerId" FOREIGN KEY ("ServerId") REFERENCES "Servers"("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Databases_ServerId_Name" ON "Databases"("ServerId", "Name");
            """);
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Databases\" ADD COLUMN IF NOT EXISTS \"DataPath\" text NULL;");
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "UptimeChecks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "EnvironmentId" uuid NOT NULL,
                "Url" text NOT NULL,
                "Ok" boolean NOT NULL,
                "StatusCode" integer NULL,
                "LatencyMs" integer NOT NULL,
                "CheckedAt" timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_UptimeChecks_EnvironmentId" ON "UptimeChecks"("EnvironmentId");
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "BackupSettings" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Enabled" boolean NOT NULL,
                "IntervalMinutes" integer NOT NULL,
                "Retention" integer NOT NULL,
                "DestDir" text NOT NULL,
                "DbContainer" text NOT NULL,
                "LastBackupAt" timestamptz NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            """);
    }
    // Only seed demo data in Development (local `dotnet run`). Production/VM
    // deployments start empty: you add your real servers & projects in the UI.
    if (app.Environment.IsDevelopment())
        SeedData.EnsureSeeded(db);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// health for Simploy itself
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0" }));

// serve React build in production (when web/dist exists)
var webDist = Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist");
if (Directory.Exists(webDist))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webDist)), RequestPath = "" });

app.Run();

// If the postgres role password has drifted from the connection string, the first
// connection fails with 28P01. Ask the Agent (which has the docker socket) to reset
// the role password in-place, then let EnsureCreated/migrations retry.
static void RepairPostgresAuth(SimployDbContext db, IConfiguration config, ILogger log)
{
    var conn = db.Database.GetDbConnection();
    try
    {
        conn.Open();
        conn.Close();
        return; // already connects — nothing to do
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "28P01")
    {
        log.LogWarning("Postgres password auth failed (28P01) — repairing postgres role password in place (no data loss)");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Could not verify postgres connection at startup");
        return; // network/other issue — don't try to repair
    }

    var password = ParsePassword(config.GetConnectionString("Default"));
    var user = ParseUsername(config.GetConnectionString("Default")) ?? "postgres";
    // Try several candidate URLs: explicit config, then Docker DNS names that
    // work on Linux without `host.docker.internal` (which only resolves on
    // Docker Desktop). Fail loud so password drift is visible in the logs.
    string[] candidates;
    var explicitBase = config["Agent:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(explicitBase))
        candidates = new[] { explicitBase };
    else
        candidates = new[] { "http://simploy-agent:8089", "http://agent:8089", "http://host.docker.internal:8089" };
    var token = config["Agent:Token"] ?? "";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    if (!string.IsNullOrEmpty(token))
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    Exception? last = null;
    foreach (var agentBase in candidates)
    {
        try
        {
            log.LogInformation("Attempting postgres password repair via {Url} (user={User})", agentBase, user);
            var resp = http.PostAsJsonAsync($"{agentBase}/system/db/fix-password",
                new Simploy.Shared.Contracts.AgentFixPasswordRequest(null, user, password)).GetAwaiter().GetResult();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode)
            {
                log.LogInformation("Postgres password repaired via agent at {Url}: {Body}", agentBase, body);
                last = null;
                break;
            }
            log.LogError("Agent repair at {Url} returned {Status}: {Body}", agentBase, (int)resp.StatusCode, body);
            last = new Exception($"agent {agentBase} returned {(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not reach agent at {Url} to repair postgres password", agentBase);
            last = ex;
        }
    }
    if (last is not null)
        log.LogError(last, "All candidate agent URLs failed; postgres password may still be drifted from connection string '{Password}'", password);
}

static string ParsePassword(string? cs)
{
    cs ??= "";
    foreach (var p in cs.Split(';'))
    {
        var kv = p.Split('=', 2);
        if (kv.Length == 2 && kv[0].Trim().Equals("Password", StringComparison.OrdinalIgnoreCase))
            return kv[1].Trim();
    }
    return "postgres";
}

static string? ParseUsername(string? cs)
{
    cs ??= "";
    foreach (var p in cs.Split(';'))
    {
        var kv = p.Split('=', 2);
        if (kv.Length == 2 && kv[0].Trim().Equals("Username", StringComparison.OrdinalIgnoreCase))
            return kv[1].Trim();
    }
    return null;
}
