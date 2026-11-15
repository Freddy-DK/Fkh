using Fkh.Services;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class CleanupFkhTests
{
    [Fact]
    public void Orphan_detection_preserves_databases_for_deployed_containers()
    {
        var orphaned = FkhCleanupFkh.FindOrphanDatabaseNames(
            ["alice-stopped", "alice-stopped-default", "alice-removed", "master", "custom"],
            ["alice-stopped"]);

        Assert.Equal(new[] { "alice-removed" }, orphaned);
    }

    [Fact]
    public void Orphan_detection_requires_a_complete_app_name_prefix()
    {
        var orphaned = FkhCleanupFkh.FindOrphanDatabaseNames(
            ["alice-app2-default"],
            ["alice-app"]);

        Assert.Equal(new[] { "alice-app2-default" }, orphaned);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    [InlineData(-2048, "-2.0 KB")]
    public void FormatBytes_uses_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, FkhCleanupFkh.FormatBytes(bytes));
    }
}