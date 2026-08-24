using Fkh.Models;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class ConfigParsingTests
{
    private static void SetEnv(string name, string? value)
        => Environment.SetEnvironmentVariable(name, value);

    [Fact]
    public void LoadOrgTeamConfig_parses_valid_json()
    {
        const string name = "TEST_ORG_TEAMS";
        SetEnv(name, "[{\"Org\":\"my-org\",\"Team\":\"fkh-members\"}]");
        try
        {
            var result = FunctionBase.LoadOrgTeamConfig(name);
            var entry = Assert.Single(result);
            Assert.Equal("my-org", entry.Org);
            Assert.Equal("fkh-members", entry.Team);
        }
        finally { SetEnv(name, null); }
    }

    [Fact]
    public void LoadOrgTeamConfig_throws_when_required_and_missing()
    {
        const string name = "TEST_ORG_TEAMS_MISSING";
        SetEnv(name, null);
        Assert.Throws<InvalidOperationException>(() => FunctionBase.LoadOrgTeamConfig(name, required: true));
    }

    [Fact]
    public void LoadOrgTeamConfig_returns_empty_when_optional_and_missing()
    {
        const string name = "TEST_ORG_TEAMS_OPTIONAL";
        SetEnv(name, null);
        Assert.Empty(FunctionBase.LoadOrgTeamConfig(name, required: false));
    }

    [Fact]
    public void LoadAllowedUsers_parses_valid_roles()
    {
        SetEnv("ALLOWED_USERS", "[{\"User\":\"alice\",\"Role\":\"admin\"},{\"User\":\"bob\",\"Role\":\"member\"}]");
        try
        {
            var users = FunctionBase.LoadAllowedUsers();
            Assert.Equal(2, users.Count);
            Assert.Equal("alice", users[0].User);
            Assert.Equal("admin", users[0].Role);
        }
        finally { SetEnv("ALLOWED_USERS", null); }
    }

    [Fact]
    public void LoadAllowedUsers_rejects_invalid_role()
    {
        SetEnv("ALLOWED_USERS", "[{\"User\":\"alice\",\"Role\":\"superuser\"}]");
        try
        {
            Assert.Throws<InvalidOperationException>(() => FunctionBase.LoadAllowedUsers());
        }
        finally { SetEnv("ALLOWED_USERS", null); }
    }

    [Fact]
    public void LoadAllowedUsers_returns_empty_when_unset()
    {
        SetEnv("ALLOWED_USERS", null);
        Assert.Empty(FunctionBase.LoadAllowedUsers());
    }

    [Fact]
    public void LoadStringList_parses_and_defaults_to_empty()
    {
        const string name = "TEST_STRING_LIST";
        SetEnv(name, "[\"a\",\"b\"]");
        try
        {
            Assert.Equal(new[] { "a", "b" }, FunctionBase.LoadStringList(name));
        }
        finally { SetEnv(name, null); }

        SetEnv(name, null);
        Assert.Empty(FunctionBase.LoadStringList(name));
    }
}
