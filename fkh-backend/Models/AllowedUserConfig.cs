namespace Fkh.Models;

/// <summary>
/// Explicit GitHub username authorization from configuration.
/// In App Settings, configure as:
///   "ALLOWED_USERS": "[{\"User\":\"octocat\",\"Role\":\"member\"}]"
/// Role must be admin, member, or support (case-insensitive).
/// </summary>
public record AllowedUserConfig(string User, string Role);
