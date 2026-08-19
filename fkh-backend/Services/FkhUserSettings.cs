using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhUserSettings : FkhServiceBase
{
    private const string SettingsContainer = "settings";
    private const string SettingsBlob = "usersettings.json";
    private const string DefaultSettingsBlob = "defaultusersettings.json";
    private const string MembersKey = "_members";
    private const string AdminsKey = "_admins";

    /// <summary>
    /// Defines known user settings, their default values, and whether only admins can change them.
    /// </summary>
    private static readonly Dictionary<string, SettingDefinition> KnownSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MaxContainers"] = new SettingDefinition { DefaultValue = JsonValue.Create(3), AdminOnly = true },
        ["Uptime"] = new SettingDefinition { DefaultValue = null, AdminOnly = true },
    };

    public FkhUserSettings(ILogger<FkhUserSettings> logger) : base(logger) { }

    /// <summary>
    /// Gets resolved settings for a user (or all users for admins).
    /// </summary>
    public async Task<object> GetSettingsAsync(Dictionary<string, string> parameters)
    {
        var callerUsername = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminValue)
            && string.Equals(adminValue, "true", StringComparison.OrdinalIgnoreCase);

        var targetUser = parameters.TryGetValue("username", out var u) ? u : null;
        var property = parameters.TryGetValue("property", out var p) ? p : null;

        var allSettings = await ReadAllSettingsAsync();
        var defaultSettings = await ReadDefaultSettingsAsync();

        // Admin with no --username: return resolved settings for all users (including _members/_admins)
        if (isAdmin && string.IsNullOrWhiteSpace(targetUser))
        {
            var result = new JsonObject();
            // Merge _members and _admins from defaults first, then override with runtime values
            var mergedMembers = MergeSpecialKey(defaultSettings, allSettings, MembersKey);
            if (mergedMembers is not null)
                result[MembersKey] = mergedMembers;
            var mergedAdmins = MergeSpecialKey(defaultSettings, allSettings, AdminsKey);
            if (mergedAdmins is not null)
                result[AdminsKey] = mergedAdmins;
            // Collect all user keys from both default and runtime settings
            var userKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in allSettings)
            {
                if (!IsSpecialKey(kvp.Key)) userKeys.Add(kvp.Key);
            }
            foreach (var kvp in defaultSettings)
            {
                if (!IsSpecialKey(kvp.Key)) userKeys.Add(kvp.Key);
            }
            // Include resolved settings for each real user
            foreach (var key in userKeys)
            {
                result[key] = ResolveUserSettings(allSettings, defaultSettings, key, false);
            }
            return result;
        }

        // Determine whose settings to return
        var username = string.IsNullOrWhiteSpace(targetUser) ? callerUsername : targetUser;

        // Non-admins can only view their own settings
        if (!isAdmin && !string.Equals(username, callerUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("You can only view your own settings.");
        }

        // Resolve settings for the user. Special keys (_members/_admins) return only their own
        // merged values (default + runtime); layering _admins over _members would be wrong here.
        var resolved = IsSpecialKey(username)
            ? MergeSpecialKey(defaultSettings, allSettings, username) ?? new JsonObject()
            : ResolveUserSettings(allSettings, defaultSettings, username, isAdmin);

        if (!string.IsNullOrWhiteSpace(property))
        {
            if (resolved.TryGetPropertyValue(property, out var value))
            {
                var propResult = new JsonObject { [username] = new JsonObject { [property] = value?.DeepClone() } };
                return propResult;
            }
            throw new InvalidOperationException($"Setting '{property}' not found.");
        }

        var userResult = new JsonObject { [username] = resolved };
        return userResult;
    }

    /// <summary>
    /// Sets a setting for a user.
    /// </summary>
    public async Task<object> SetSettingsAsync(Dictionary<string, string> parameters)
    {
        var callerUsername = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminValue)
            && string.Equals(adminValue, "true", StringComparison.OrdinalIgnoreCase);

        var targetUser = parameters.TryGetValue("username", out var u) ? u : null;
        var property = parameters["property"];
        var value = parameters["value"];

        // Determine whose settings to update
        var username = string.IsNullOrWhiteSpace(targetUser) ? callerUsername : targetUser;

        // Only admins can modify _members and _admins
        if (IsSpecialKey(username) && !isAdmin)
        {
            throw new UnauthorizedAccessException($"Only admins can modify '{username}' settings.");
        }

        // Non-admins can only modify their own settings
        if (!isAdmin && !string.Equals(username, callerUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("You can only modify your own settings.");
        }

        // Check if the setting is admin-only
        if (KnownSettings.TryGetValue(property, out var def) && def.AdminOnly && !isAdmin)
        {
            throw new UnauthorizedAccessException($"Setting '{property}' can only be modified by admins.");
        }

        var allSettings = await ReadAllSettingsAsync();

        // Get or create the user's settings object
        if (!allSettings.TryGetPropertyValue(username, out var userNode) || userNode is not JsonObject userObj)
        {
            userObj = new JsonObject();
            allSettings[username] = userObj;
        }

        // Parse value as JSON if possible, otherwise store as string
        JsonNode? jsonValue;
        try
        {
            jsonValue = JsonNode.Parse(value);
        }
        catch
        {
            jsonValue = JsonValue.Create(value);
        }

        userObj[property] = jsonValue?.DeepClone();

        await WriteAllSettingsAsync(allSettings);

        Logger.LogInformation("Setting '{Property}' set to '{Value}' for user '{User}' by '{Caller}'.",
            property, value, username, callerUsername);

        return new { user = username, property, value = jsonValue };
    }

    /// <summary>
    /// Clears settings for a user. Admin only.
    /// If a property is specified, only that property is removed.
    /// If no property is specified, all user-specific settings are removed.
    /// </summary>
    public async Task<object> ClearSettingsAsync(Dictionary<string, string> parameters)
    {
        var callerUsername = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminValue)
            && string.Equals(adminValue, "true", StringComparison.OrdinalIgnoreCase);

        if (!isAdmin)
        {
            throw new UnauthorizedAccessException("Only admins can clear settings.");
        }

        var targetUser = parameters.TryGetValue("username", out var u) ? u : null;
        var property = parameters.TryGetValue("property", out var p) ? p : null;

        if (string.IsNullOrWhiteSpace(targetUser))
        {
            throw new InvalidOperationException("Parameter 'username' is required for ClearSettings.");
        }

        var username = targetUser;
        var allSettings = await ReadAllSettingsAsync();

        if (!allSettings.TryGetPropertyValue(username, out var userNode) || userNode is not JsonObject userObj)
        {
            return new { user = username, message = "No settings to clear." };
        }

        if (!string.IsNullOrWhiteSpace(property))
        {
            if (userObj.Remove(property))
            {
                await WriteAllSettingsAsync(allSettings);
                Logger.LogInformation("Setting '{Property}' cleared for user '{User}' by admin '{Caller}'.",
                    property, username, callerUsername);
                return new { user = username, cleared = property };
            }
            return new { user = username, message = $"Setting '{property}' not found." };
        }

        // Remove all settings for the user
        allSettings.Remove(username);
        await WriteAllSettingsAsync(allSettings);

        Logger.LogInformation("All settings cleared for user '{User}' by admin '{Caller}'.",
            username, callerUsername);
        return new { user = username, cleared = "all" };
    }

    /// <summary>
    /// Resolves the effective settings for a specific user by merging (lowest to highest priority):
    /// default _members → runtime _members → default _admins (if admin) → runtime _admins (if admin) → user-specific.
    /// </summary>
    internal static JsonObject ResolveUserSettings(JsonObject allSettings, JsonObject defaultSettings, string username, bool isAdmin)
    {
        var result = new JsonObject();

        // 1. Start with default _members
        if (defaultSettings.TryGetPropertyValue(MembersKey, out var defMembersNode) && defMembersNode is JsonObject defMembersObj)
        {
            foreach (var kvp in defMembersObj)
                result[kvp.Key] = kvp.Value?.DeepClone();
        }

        // 2. Override with runtime _members
        if (allSettings.TryGetPropertyValue(MembersKey, out var membersNode) && membersNode is JsonObject membersObj)
        {
            foreach (var kvp in membersObj)
                result[kvp.Key] = kvp.Value?.DeepClone();
        }

        // 3. Override with default _admins (if admin)
        if (isAdmin && defaultSettings.TryGetPropertyValue(AdminsKey, out var defAdminsNode) && defAdminsNode is JsonObject defAdminsObj)
        {
            foreach (var kvp in defAdminsObj)
                result[kvp.Key] = kvp.Value?.DeepClone();
        }

        // 4. Override with runtime _admins (if admin)
        if (isAdmin && allSettings.TryGetPropertyValue(AdminsKey, out var adminsNode) && adminsNode is JsonObject adminsObj)
        {
            foreach (var kvp in adminsObj)
                result[kvp.Key] = kvp.Value?.DeepClone();
        }

        // 5. Override with user-specific settings
        if (!IsSpecialKey(username) && allSettings.TryGetPropertyValue(username, out var userNode) && userNode is JsonObject userObj)
        {
            foreach (var kvp in userObj)
                result[kvp.Key] = kvp.Value?.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// Gets a specific resolved setting value for a user, returning the default if not set.
    /// </summary>
    public async Task<JsonNode?> GetResolvedSettingAsync(string username, bool isAdmin, string settingName)
    {
        var allSettings = await ReadAllSettingsAsync();
        var defaultSettings = await ReadDefaultSettingsAsync();
        var resolved = ResolveUserSettings(allSettings, defaultSettings, username, isAdmin);

        if (resolved.TryGetPropertyValue(settingName, out var value))
        {
            return value;
        }

        // Fall back to the built-in default
        if (KnownSettings.TryGetValue(settingName, out var def))
        {
            return def.DefaultValue?.DeepClone();
        }

        return null;
    }

    /// <summary>
    /// Gets a global (cluster-wide) setting stored under '_admins'. The runtime value in
    /// usersettings.json overrides the Terraform-seeded default in defaultusersettings.json.
    /// </summary>
    public async Task<JsonNode?> GetGlobalSettingAsync(string property)
    {
        var allSettings = await ReadAllSettingsAsync();
        var defaultSettings = await ReadDefaultSettingsAsync();

        JsonNode? result = null;
        if (defaultSettings.TryGetPropertyValue(AdminsKey, out var defNode) && defNode is JsonObject defObj
            && defObj.TryGetPropertyValue(property, out var defValue))
            result = defValue?.DeepClone();
        if (allSettings.TryGetPropertyValue(AdminsKey, out var rtNode) && rtNode is JsonObject rtObj
            && rtObj.TryGetPropertyValue(property, out var rtValue))
            result = rtValue?.DeepClone();
        return result;
    }

    private async Task<JsonObject> ReadAllSettingsAsync()
    {
        return await ReadBlobAsync(SettingsBlob);
    }

    private async Task<JsonObject> ReadDefaultSettingsAsync()
    {
        return await ReadBlobAsync(DefaultSettingsBlob);
    }

    private async Task<JsonObject> ReadBlobAsync(string blobName)
    {
        var blobClient = GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync())
        {
            return new JsonObject();
        }

        var downloadResponse = await blobClient.DownloadContentAsync();
        var json = downloadResponse.Value.Content.ToString();

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch
        {
            Logger.LogWarning("Failed to parse settings blob '{BlobName}'. Starting with empty settings.", blobName);
            return new JsonObject();
        }
    }

    private async Task WriteAllSettingsAsync(JsonObject settings)
    {
        var blobClient = GetBlobClient();

        var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        await blobClient.UploadAsync(stream, overwrite: true);
    }

    private BlobClient GetBlobClient(string blobName = SettingsBlob)
    {
#pragma warning disable CS0618
        var credential = new ManagedIdentityCredential(ClientId);
#pragma warning restore CS0618
        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{DbsStorageAccountName}.blob.core.windows.net"), credential);
        var containerClient = blobServiceClient.GetBlobContainerClient(SettingsContainer);
        return containerClient.GetBlobClient(blobName);
    }

    private static bool IsSpecialKey(string key) =>
        string.Equals(key, MembersKey, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, AdminsKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Merges a special key (_members or _admins) from default and runtime settings.
    /// Default values are applied first, then runtime values override.
    /// </summary>
    private static JsonObject? MergeSpecialKey(JsonObject defaultSettings, JsonObject allSettings, string key)
    {
        var hasDefault = defaultSettings.TryGetPropertyValue(key, out var defNode) && defNode is JsonObject defObj;
        var hasRuntime = allSettings.TryGetPropertyValue(key, out var rtNode) && rtNode is JsonObject rtObj;

        if (!hasDefault && !hasRuntime) return null;

        var merged = new JsonObject();
        if (hasDefault)
        {
            foreach (var kvp in (JsonObject)defNode!)
                merged[kvp.Key] = kvp.Value?.DeepClone();
        }
        if (hasRuntime)
        {
            foreach (var kvp in (JsonObject)rtNode!)
                merged[kvp.Key] = kvp.Value?.DeepClone();
        }
        return merged;
    }

    private sealed class SettingDefinition
    {
        public JsonNode? DefaultValue { get; init; }
        public bool AdminOnly { get; init; }
    }
}
