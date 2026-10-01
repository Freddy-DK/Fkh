using Xunit;

namespace Fkh.Cli.UnitTests;

public class MergeTfvarsTests
{
    private const string Template = """
        fkhDeploymentName = "myorg"

        # registration = {
        #   company = "My Company"
        # }
        location = "westeurope"
        """;

    [Fact]
    public void Preserves_existing_values_for_template_keys()
    {
        var merged = UpdateDeploymentRepoCommand.MergeTfvars("fkhDeploymentName = \"contoso\"\nlocation = \"swedencentral\"", Template);
        Assert.Contains("fkhDeploymentName = \"contoso\"", merged);
        Assert.Contains("location = \"swedencentral\"", merged);
    }

    [Fact]
    public void Keeps_active_value_for_setting_only_commented_out_in_template()
    {
        var old = "fkhDeploymentName = \"contoso\"\nregistration = {\n  company = \"Contoso\"\n}\nlocation = \"westeurope\"";
        var merged = UpdateDeploymentRepoCommand.MergeTfvars(old, Template);

        var lines = merged.Split('\n');
        var active = Array.FindIndex(lines, l => l.StartsWith("registration = {"));
        var example = Array.FindIndex(lines, l => l.StartsWith("# registration = {"));
        Assert.True(active >= 0, merged);
        Assert.True(active < example, merged);
        Assert.Contains("  company = \"Contoso\"", merged);
        Assert.Single(lines, l => l.StartsWith("registration = {"));
    }

    [Fact]
    public void Leaves_commented_example_alone_when_not_set()
    {
        var merged = UpdateDeploymentRepoCommand.MergeTfvars("fkhDeploymentName = \"contoso\"", Template);
        Assert.DoesNotContain("\nregistration = {", merged);
        Assert.Contains("# registration = {", merged);
    }
}
