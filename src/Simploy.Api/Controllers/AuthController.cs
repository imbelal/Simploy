using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Simploy.Api.Services;
using Simploy.Shared.Contracts;

namespace Simploy.Api.Controllers;

[ApiController, Route("api/auth")]
public class AuthController(AuthService auth) : ControllerBase
{
    [HttpPost("login"), AllowAnonymous]
    public IActionResult Login(LoginRequest req)
    {
        if (!auth.Validate(req.Username, req.Password))
            return Unauthorized(new { error = "Invalid username or password" });
        return Ok(new { token = auth.CreateToken(req.Username), username = req.Username });
    }

    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new { username = User.Identity?.Name ?? "?" });
}
