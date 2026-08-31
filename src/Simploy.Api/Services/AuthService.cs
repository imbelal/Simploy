using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Simploy.Api.Services;

public class AuthService(IConfiguration cfg)
{
    private string Secret => cfg["Auth:JwtSecret"] ?? "simploy-dev-secret-change-me";
    private string Issuer => cfg["Auth:JwtIssuer"] ?? "simploy";
    private string Audience => cfg["Auth:JwtAudience"] ?? "simploy";

    public string AdminUser => cfg["Auth:AdminUser"] ?? "";
    private string AdminPassword => cfg["Auth:AdminPassword"] ?? "";

    public bool Validate(string username, string password) =>
        !string.IsNullOrEmpty(AdminUser) && username == AdminUser && password == AdminPassword;

    public string CreateToken(string username)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.UniqueName, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var jwt = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims,
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
