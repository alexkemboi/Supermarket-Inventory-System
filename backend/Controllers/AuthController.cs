using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using Inventory.Api.Contracts;
using Inventory.Api.Data;
using Inventory.Api.Models;
using Inventory.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(AppDbContext db, TokenService tokens, IPasswordHasher<User> hasher, TimeProvider clock)
    : ControllerBase
{
    private const int MinPasswordLength = 8;

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var email = NormalizeEmail(request.Email);

        if (name.Length is 0 or > 100) return BadRequest(new { error = "Name is required (max 100 characters)." });
        if (email is null) return BadRequest(new { error = "A valid email address is required." });
        if ((request.Password?.Length ?? 0) < MinPasswordLength)
            return BadRequest(new { error = $"Password must be at least {MinPasswordLength} characters." });

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { error = "An account with this email already exists." });

        var user = new User { Name = name, Email = email, CreatedAt = clock.GetUtcNow().UtcDateTime };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent sign-up for the same email; the unique index rejected it.
            return Conflict(new { error = "An account with this email already exists." });
        }

        return tokens.Issue(user);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = email is null ? null : await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

        // Same response for unknown email and wrong password, so accounts cannot be enumerated.
        if (user is null || string.IsNullOrEmpty(request.Password)
            || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            return Unauthorized(new { error = "Invalid email or password." });

        return tokens.Issue(user);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id)) return Unauthorized();

        var user = await db.Users
            .Where(u => u.Id == id)
            .Select(u => new UserDto(u.Id, u.Name, u.Email))
            .SingleOrDefaultAsync(ct);

        return user is null ? Unauthorized() : user;
    }

    private static string? NormalizeEmail(string? email)
    {
        email = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 256) return null;
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email ? email : null;
    }
}
