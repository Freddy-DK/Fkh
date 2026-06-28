namespace Fkh.Services;

public class FkhGetCurrentUser
{
    public Task<object> GetCurrentUserAsync(Dictionary<string, string> parameters)
    {
        var username = parameters["_githubUsername"];
        var isAdmin = parameters.TryGetValue("_isAdmin", out var adminValue)
            && string.Equals(adminValue, "true", StringComparison.OrdinalIgnoreCase);
        var isSupport = parameters.TryGetValue("_isSupport", out var supportValue)
            && string.Equals(supportValue, "true", StringComparison.OrdinalIgnoreCase);

        return Task.FromResult<object>(new
        {
            Username = username,
            IsAdmin = isAdmin,
            IsSupport = isSupport,
        });
    }
}