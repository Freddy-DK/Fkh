using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fkh.Services;

/// <summary>
/// Validates Azure DevOps OIDC tokens and checks the subject claim against the allowed connections list.
/// Supports two token formats:
///   1. Azure DevOps native (issuer: https://vstoken.dev.azure.com/{org-id}, subject: sc://org/project/connection)
///   2. Entra ID (issuer: https://login.microsoftonline.com/{tenant}/v2.0, subject contains /sc/{org-id})
/// </summary>
public class AdoOidcService
{
    private static readonly List<AdoConnection> AllowedConnections = LoadAllowedConnections();
    private static readonly string? TenantId = Environment.GetEnvironmentVariable("AAD_TENANT_ID");

    private static readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> ConfigManagers = CreateConfigManagers();

    /// <summary>
    /// Returns true if the token's issuer matches a known Azure DevOps OIDC issuer (vstoken or Entra ID format).
    /// </summary>
    public static bool IsAdoOidcToken(string token)
    {
        if (!token.StartsWith("eyJ", StringComparison.Ordinal))
            return false;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            // Format 1: Azure DevOps native issuer
            if (jwt.Issuer.StartsWith("https://vstoken.dev.azure.com/", StringComparison.OrdinalIgnoreCase))
                return true;

            // Format 2: Entra ID issuer with ADO service connection subject
            if (!string.IsNullOrEmpty(TenantId) &&
                string.Equals(jwt.Issuer, $"https://login.microsoftonline.com/{TenantId}/v2.0", StringComparison.OrdinalIgnoreCase) &&
                jwt.Subject?.Contains("/sc/", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates an Azure DevOps OIDC token and returns the subject claim (e.g. "sc://org/project/connection").
    /// Returns null if validation fails or the connection is not in the allow-list.
    /// </summary>
    public async Task<(string? Subject, string? Error)> ValidateTokenAsync(string token)
    {
        if (AllowedConnections.Count == 0)
            return (null, "No allowed connections configured");

        var handler = new JwtSecurityTokenHandler();
        JwtSecurityToken unvalidatedJwt;
        try
        {
            unvalidatedJwt = handler.ReadJwtToken(token);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read JWT: {ex.Message}");
        }

        var issuer = unvalidatedJwt.Issuer;
        var tokenAudiences = string.Join(", ", unvalidatedJwt.Audiences);
        Console.WriteLine($"ADO OIDC token — issuer: {issuer}, audiences: {tokenAudiences}, subject: {unvalidatedJwt.Subject}");
        if (!ConfigManagers.TryGetValue(issuer.ToLowerInvariant(), out var configManager))
            return (null, $"No config manager for issuer: {issuer} (known: {string.Join(", ", ConfigManagers.Keys)})");

        var config = await configManager.GetConfigurationAsync(CancellationToken.None);
        var isEntraFormat = issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase);
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        try
        {
            handler.ValidateToken(token, validationParameters, out var validatedToken);
            var jwt = (JwtSecurityToken)validatedToken;

            var subject = jwt.Subject;
            if (string.IsNullOrEmpty(subject))
                return (null, "Token has no subject claim");

            if (isEntraFormat)
            {
                var matchingConnection = AllowedConnections.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.EntraSubject) &&
                    string.Equals(c.EntraSubject, subject, StringComparison.Ordinal));

                if (matchingConnection is null)
                    return (null, $"Entra subject not in allow-list. Subject: {subject}, Expected: {string.Join(", ", AllowedConnections.Where(c => !string.IsNullOrWhiteSpace(c.EntraSubject)).Select(c => c.EntraSubject))}");

                return ($"sc://{matchingConnection.DevopsOrg}/{matchingConnection.DevopsProject}/{matchingConnection.DevopsConnectionName}", null);
            }

            // vstoken format: subject is sc://org/project/connection
            var isVstokenAllowed = AllowedConnections.Any(c =>
                string.Equals($"sc://{c.DevopsOrg}/{c.DevopsProject}/{c.DevopsConnectionName}", subject, StringComparison.OrdinalIgnoreCase));

            if (!isVstokenAllowed)
                return (null, $"vstoken subject not in allow-list. Subject: {subject}, Expected: {string.Join(", ", AllowedConnections.Select(c => $"sc://{c.DevopsOrg}/{c.DevopsProject}/{c.DevopsConnectionName}"))}");

            return (subject, null);
        }
        catch (SecurityTokenException ex)
        {
            return (null, $"Token validation failed: {ex.Message}");
        }
    }

    private static List<AdoConnection> LoadAllowedConnections()
    {
        var raw = Environment.GetEnvironmentVariable("ALLOWED_ADO_CONNECTIONS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return JsonSerializer.Deserialize<List<AdoConnection>>(raw, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }) ?? [];
    }

    private static Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> CreateConfigManagers()
    {
        var managers = new Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var conn in AllowedConnections)
        {
            // Format 1: Azure DevOps native issuer
            var vstokenIssuer = $"https://vstoken.dev.azure.com/{conn.DevopsOrgId}";
            var vstokenKey = vstokenIssuer.ToLowerInvariant();
            if (!managers.ContainsKey(vstokenKey))
            {
                managers[vstokenKey] = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"{vstokenIssuer}/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever());
            }
        }

        // Format 2: Entra ID issuer (one per tenant)
        if (!string.IsNullOrEmpty(TenantId) && AllowedConnections.Count > 0)
        {
            var entraIssuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
            var entraKey = entraIssuer.ToLowerInvariant();
            if (!managers.ContainsKey(entraKey))
            {
                managers[entraKey] = new ConfigurationManager<OpenIdConnectConfiguration>(
                    $"https://login.microsoftonline.com/{TenantId}/v2.0/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever());
            }
        }

        return managers;
    }

    private sealed class AdoConnection
    {
        public string DevopsOrgId { get; set; } = "";
        public string DevopsOrg { get; set; } = "";
        public string DevopsProject { get; set; } = "";
        public string DevopsConnectionName { get; set; } = "";
        public string EntraSubject { get; set; } = "";
    }
}
