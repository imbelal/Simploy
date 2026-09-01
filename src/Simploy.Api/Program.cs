using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddHostedService<DeploymentWorker>();

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
    db.Database.EnsureCreated();
    // EnsureCreated does not alter an existing database, so apply lightweight
    // additive schema changes here (idempotent, Postgres only).
    if (db.Database.IsRelational())
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Domains\" ADD COLUMN IF NOT EXISTS \"EnableHttps\" boolean NOT NULL DEFAULT false; ALTER TABLE \"Projects\" ADD COLUMN IF NOT EXISTS \"GithubInstallationId\" text NULL;");
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
