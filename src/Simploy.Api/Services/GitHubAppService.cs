using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Simploy.Api.Services;

/// <summary>
/// Talks to the GitHub App API: mints a short-lived app JWT, exchanges it for a
/// repository-scoped installation token (valid ~1h), and lists installations so a
/// project can be bound to one. No long-lived PATs are needed.
/// </summary>
public class GitHubAppService(IConfiguration cfg)
{
    // Env: GithubApp__AppId, GithubApp__ClientId, GithubApp__ClientSecret, GithubApp__PrivateKey / PrivateKeyFile, GithubApp__Slug
    private string AppId => cfg["GithubApp:AppId"] ?? "";
    private string ClientId => cfg["GithubApp:ClientId"] ?? "";
    private string Slug => cfg["GithubApp:Slug"] ?? "";
    private string PrivateKeyPem
    {
        get
        {
            // Prefer a file (robust for PEM, no newline/quoting issues in env).
            var file = cfg["GithubApp:PrivateKeyFile"];
            if (!string.IsNullOrEmpty(file) && File.Exists(file)) return File.ReadAllText(file);
            return cfg["GithubApp:PrivateKey"] ?? "";
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(AppId) && !string.IsNullOrEmpty(PrivateKeyPem);
    // Owner install URL: works for your own app regardless of the slug (public /apps/... slug URL
    // 404s when the slug differs or the app is private).
    public string InstallUrl => $"https://github.com/settings/installations/{AppId}";

    /// <summary>Short-lived JWT for the GitHub App (iss = app id).</summary>
    public string CreateAppJwt()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(PrivateKeyPem);
        var creds = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
        var now = DateTimeOffset.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Iss, AppId),
            new Claim(JwtRegisteredClaimNames.Iat, now.AddMinutes(-1).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Exp, now.AddMinutes(10).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
        var jwt = new JwtSecurityToken(claims: claims, signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("simploy");
        return http;
    }

    /// <summary>Exchanges the app JWT for an installation token (scoped, ~1h).</summary>
    public async Task<string> GetInstallationTokenAsync(string installationId, CancellationToken ct = default)
    {
        using var http = NewClient();
        using var resp = await http.PostAsync($"https://api.github.com/app/installations/{installationId}/access_tokens",
            new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("token").GetString()!;
    }

    /// <summary>Lists branches + default branch for a repo. Uses the installation token for
    /// private repos, or the public API (no auth) for public ones.</summary>
    public async Task<(List<string> branches, string defaultBranch)> GetBranchesAsync(string ownerRepo, string? installationId, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{ownerRepo}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("simploy");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(installationId) && IsConfigured)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetInstallationTokenAsync(installationId, ct));

        var repoJson = await http.GetFromJsonAsync<JsonElement>(url, ct);
        var def = repoJson.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";

        var b = await http.GetFromJsonAsync<JsonElement>($"{url}/branches?per_page=100", ct);
        var branches = b.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();
        return (branches, def);
    }

    /// <summary>Lists the installations this app is installed on (account + id).</summary>
    public async Task<List<GitHubInstallationDto>> ListInstallationsAsync(CancellationToken ct = default)
    {
        using var http = NewClient();
        var arr = await http.GetFromJsonAsync<JsonElement>("https://api.github.com/app/installations", ct);
        var list = new List<GitHubInstallationDto>();
        foreach (var el in arr.EnumerateArray())
        {
            var id = el.GetProperty("id").GetInt64().ToString();
            var login = el.TryGetProperty("account", out var a) && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            list.Add(new GitHubInstallationDto(id, login));
        }
        return list;
    }
    /// <summary>Lists the repos the given installation can access (using its token).</summary>
    public async Task<List<GitHubRepoDto>> ListRepositoriesAsync(string installationId, CancellationToken ct = default)
    {
        var token = await GetInstallationTokenAsync(installationId, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("simploy");
        var json = await http.GetFromJsonAsync<JsonElement>("https://api.github.com/installation/repositories?per_page=100", ct);
        var list = new List<GitHubRepoDto>();
        foreach (var r in json.GetProperty("repositories").EnumerateArray())
        {
            list.Add(new GitHubRepoDto(
                r.GetProperty("full_name").GetString()!,
                r.GetProperty("name").GetString()!,
                r.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main",
                r.TryGetProperty("private", out var p) && p.GetBoolean()));
        }
        return list;
    }
}

public record GitHubInstallationDto(string Id, string Account);
public record GitHubRepoDto(string FullName, string Name, string DefaultBranch, bool IsPrivate);
