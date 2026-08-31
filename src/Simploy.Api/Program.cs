using Microsoft.EntityFrameworkCore;
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
builder.Services.AddHostedService<DeploymentWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SimployDbContext>();
    db.Database.EnsureCreated();
    // Only seed demo data in Development (local `dotnet run`). Production/VM
    // deployments start empty: you add your real servers & projects in the UI.
    if (app.Environment.IsDevelopment())
        SeedData.EnsureSeeded(db);
}

app.UseCors();
app.MapControllers();

// health for Simploy itself
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0" }));

// serve React build in production (when web/dist exists)
var webDist = Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist");
if (Directory.Exists(webDist))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webDist)), RequestPath = "" });

app.Run();
