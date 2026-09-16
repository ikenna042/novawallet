using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Infrastructure;
using NovaWallet.Application;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    public sealed record RegisterRequest(
        [Required, StringLength(Credentials.MaxEmailLength)] string? Email,
        [Required, StringLength(Credentials.MaxPasswordLength)] string? Password,
        [StringLength(Credentials.MaxFullNameLength)] string? FullName);

    public sealed record LoginRequest([Required] string? Email, [Required] string? Password);

    public sealed record RefreshRequest([Required, StringLength(128)] string? RefreshToken);

    /// <summary>
    /// Create a customer account. Password: 10-128 characters with at least one letter and one digit.
    /// Administrators can't be created here; they are seeded or promoted by another admin.
    /// </summary>
    [HttpPost("register")]
    [ApiMessage("Account created")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<ApiResponse<UserProfile>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var profile = await auth.RegisterAsync(new RegisterCommand(request.Email, request.Password, request.FullName), ct);
        return StatusCode(StatusCodes.Status201Created, profile);
    }

    /// <summary>
    /// Sign in. Returns a 15-minute access token (use it as <c>Authorization: Bearer …</c>) and a refresh token.
    /// Any failure (unknown email, wrong password, disabled or locked account) returns the same 401.
    /// Five wrong passwords lock the account for 15 minutes.
    /// </summary>
    [HttpPost("login")]
    [ApiMessage("Signed in")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<ApiResponse<AuthTokens>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public Task<AuthTokens> Login([FromBody] LoginRequest request, CancellationToken ct) =>
        auth.LoginAsync(new LoginCommand(request.Email, request.Password), ct);

    /// <summary>
    /// Exchange a refresh token for a new token pair. Each refresh token works once; reusing an old one
    /// signs the whole session out (it indicates the token was stolen).
    /// </summary>
    [HttpPost("refresh")]
    [ApiMessage("Session refreshed")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<ApiResponse<AuthTokens>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public Task<AuthTokens> Refresh([FromBody] RefreshRequest request, CancellationToken ct) =>
        auth.RefreshAsync(request.RefreshToken, ct);

    /// <summary>Sign out: revokes the session that the refresh token belongs to.</summary>
    [HttpPost("logout")]
    [ApiMessage("Signed out")]
    [ProducesResponseType<ApiResponse<object>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(User.ToActor(), request.RefreshToken, ct);
        return new ObjectResult(null) { StatusCode = StatusCodes.Status200OK };
    }

    /// <summary>The signed-in user's profile, role and wallet id.</summary>
    [HttpGet("me")]
    [ApiMessage("Profile retrieved")]
    [ProducesResponseType<ApiResponse<UserProfile>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status401Unauthorized)]
    public Task<UserProfile> Me(CancellationToken ct) => auth.MeAsync(User.ToActor(), ct);
}
