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
}