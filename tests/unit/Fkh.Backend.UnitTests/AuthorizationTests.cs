using Xunit;

namespace Fkh.Backend.UnitTests;

public class AuthorizationTests
{
    [Fact]
    public void Admin_can_access_any_container()
    {
        Assert.True(FunctionBase.CanAccessContainer("alice", isAdmin: true, "bob-project"));
    }

    [Fact]
    public void Owner_can_access_own_prefixed_container()
    {
        Assert.True(FunctionBase.CanAccessContainer("alice", isAdmin: false, "alice-project"));
    }

    [Fact]
    public void Non_owner_cannot_access_another_users_container()
    {
        Assert.False(FunctionBase.CanAccessContainer("alice", isAdmin: false, "bob-project"));
    }

    [Fact]
    public void Container_named_exactly_as_username_is_not_owned()
    {
        // Ownership requires the "<user>-" prefix, so a bare match must be denied.
        Assert.False(FunctionBase.CanAccessContainer("alice", isAdmin: false, "alice"));
    }

    [Theory]
    [InlineData("Alice", "alice-project")]
    [InlineData("alice", "Alice-Project")]
    [InlineData("al.ice", "al-ice-project")]
    [InlineData("al_ice", "al-ice-project")]
    public void Ownership_match_is_case_and_separator_insensitive(string user, string appName)
    {
        Assert.True(FunctionBase.CanAccessContainer(user, isAdmin: false, appName));
    }

    [Fact]
    public void Common_container_is_accessible_to_any_user()
    {
        // COMMON_CONTAINERS is seeded with ["shared-*","demo"] in TestBootstrap.
        Assert.True(FunctionBase.CanAccessContainer("alice", isAdmin: false, "shared-thing"));
        Assert.True(FunctionBase.CanAccessContainer("bob", isAdmin: false, "demo"));
    }

    [Theory]
    [InlineData("shared-x", true)]
    [InlineData("SHARED-X", true)]
    [InlineData("shared-", true)]
    [InlineData("demo", true)]
    [InlineData("shared", false)]
    [InlineData("demonstration", false)]
    [InlineData("other", false)]
    public void IsCommonContainer_matches_wildcard_patterns_anchored(string appName, bool expected)
    {
        Assert.Equal(expected, FunctionBase.IsCommonContainer(appName));
    }
}
