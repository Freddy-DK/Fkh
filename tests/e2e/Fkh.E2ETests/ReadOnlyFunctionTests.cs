using System.Text.Json;
using Xunit;

namespace Fkh.E2ETests;

// Invokes read-only backend functions through the CLI and asserts they return JSON.
// These run whenever FKH_E2E_BACKEND_URL is configured (no --extensive needed).
public class ReadOnlyFunctionTests : E2ETest
{
    public static TheoryData<string> ParameterlessSafeCommands =>
    [
        "GetCurrentUser",
        "ListContainers",
        "ListImages",
        "ListDatabases",
        "ListFiles",
    ];

    [Theory]
    [MemberData(nameof(ParameterlessSafeCommands))]
    public void Read_only_command_returns_json(string command)
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        var json = FkhCli.RunJson(command);
        Assert.True(json.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
            $"{command} returned unexpected JSON kind {json.ValueKind}.");
    }

    [Fact]
    public void GetCurrentUser_reports_a_username()
    {
        Assert.SkipUnless(E2EConfig.IsConfigured, "FKH_E2E_BACKEND_URL is not set.");

        var json = FkhCli.RunJson("GetCurrentUser");

        // Response shape is camelCase; accept any of the common username-bearing fields.
        var hasUser = json.ValueKind == JsonValueKind.Object &&
            (json.TryGetProperty("username", out _) ||
             json.TryGetProperty("login", out _) ||
             json.TryGetProperty("user", out _));
        Assert.True(hasUser, $"GetCurrentUser did not include a username field. Payload: {json}");
    }
}
