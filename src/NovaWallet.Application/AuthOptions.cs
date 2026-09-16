namespace NovaWallet.Application;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Short-lived so a leaked access token is only useful briefly.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    public SeedAdminOptions SeedAdmin { get; set; } = new();
}

/// <summary>Creates the first administrator at startup if it doesn't exist yet.</summary>
public sealed class SeedAdminOptions
{
    /// <summary>The compose demo default; the service logs a warning while it is in use.</summary>
    public const string DemoPassword = "ChangeMe-Admin-2026!";

    public string? Email { get; set; }
    public string? Password { get; set; }
    public string FullName { get; set; } = "NovaWallet Administrator";
}
