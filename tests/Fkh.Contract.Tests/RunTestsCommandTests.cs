using System.Text;
using Xunit;

namespace Fkh.Contract.Tests;

public sealed class RunTestsCommandTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"fkh-runtests-{Guid.NewGuid():N}");

    [Fact]
    public void ValidateParametersReturnsNarrowRequest()
    {
        var outputPath = Path.Combine(_tempDirectory, "TestResults.xml");

        var request = RunTestsCommand.ValidateParameters(
        [
            "runtests",
            "--name", "owner-container",
            "--tenant", "tenant-2",
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--appName", "Test App",
            "--output", outputPath,
            "--useOIDC"
        ]);

        Assert.Equal("owner-container", request.Name);
        Assert.Equal("tenant-2", request.Tenant);
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.ExtensionId);
        Assert.Equal("Test App", request.AppName);
        Assert.Equal(30, request.TimeoutMinutes);
        Assert.Equal(outputPath, request.Output);
    }

    [Fact]
    public void ValidateParametersAcceptsTimeoutMinutes()
    {
        var request = RunTestsCommand.ValidateParameters(
        [
            "runtests",
            "--name", "owner-container",
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--timeoutMinutes", "90",
            "--output", "result.xml"
        ]);

        Assert.Equal(90, request.TimeoutMinutes);
        Assert.Equal("90", request.ToParameters()["timeoutMinutes"]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void ValidateParametersRejectsInvalidTimeoutMinutes(string timeoutMinutes)
    {
        Assert.Throws<InvalidOperationException>(() => RunTestsCommand.ValidateParameters(
        [
            "runtests",
            "--name", "owner-container",
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--timeoutMinutes", timeoutMinutes,
            "--output", "result.xml"
        ]));
    }

    [Theory]
    [InlineData("--extensionId", "11111111-1111-1111-1111-111111111111", "--output", "result.xml")]
    [InlineData("--name", "owner-container", "--output", "result.xml")]
    [InlineData("--name", "owner-container", "--extensionId", "11111111-1111-1111-1111-111111111111")]
    [InlineData("--name", "owner-container", "--extensionId", "not-a-guid", "--output", "result.xml")]
    public void ValidateParametersRejectsMissingOrInvalidRequiredInput(params string[] args)
    {
        Assert.Throws<InvalidOperationException>(() => RunTestsCommand.ValidateParameters(["runtests", .. args]));
    }

    [Theory]
    [InlineData("--name")]
    [InlineData("--tenant")]
    [InlineData("--extensionId")]
    [InlineData("--appName")]
    [InlineData("--output")]
    public void ValidateParametersRejectsOptionsWithoutValues(string option)
    {
        var args = new List<string>
        {
            "runtests",
            "--name", "owner-container",
            "--tenant", "default",
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--appName", "Test App",
            "--output", "result.xml"
        };
        args.RemoveAt(args.IndexOf(option) + 1);

        var exception = Assert.Throws<InvalidOperationException>(() => RunTestsCommand.ValidateParameters([.. args]));

        Assert.Equal($"Missing value for {option}", exception.Message);
    }

    [Theory]
    [InlineData("--tennant", "default")]
    [InlineData("--open")]
    [InlineData("--nowait")]
    public void ValidateParametersRejectsUnknownOrUnsupportedOptions(params string[] option)
    {
        Assert.Throws<InvalidOperationException>(() => RunTestsCommand.ValidateParameters(
        [
            "runtests",
            "--name", "owner-container",
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--output", "result.xml",
            .. option
        ]));
    }

    [Theory]
    [InlineData("unsafe/container", "default")]
    [InlineData("owner-container", "../default")]
    [InlineData("owner-container", "default'; Remove-Item C:\\*")]
    [InlineData("owner-container", "")]
    public void ValidateParametersRejectsUnsafeContainerOrTenant(string name, string tenant)
    {
        Assert.Throws<InvalidOperationException>(() => RunTestsCommand.ValidateParameters(
        [
            "runtests",
            "--name", name,
            "--tenant", tenant,
            "--extensionId", "11111111-1111-1111-1111-111111111111",
            "--output", "result.xml"
        ]));
    }

    [Theory]
    [InlineData("passed", 0)]
    [InlineData("failed", 1)]
    public void MaterializeResultWritesExactJUnitBeforeReturningOutcome(string outcome, int expectedExitCode)
    {
        var junit = outcome == "passed"
            ? "<testsuite tests=\"1\" failures=\"0\" errors=\"0\"><testcase name=\"Green\" /></testsuite>"
            : "<testsuite tests=\"1\" failures=\"1\" errors=\"0\"><testcase name=\"Red\"><failure message=\"Expected\" /></testcase></testsuite>";
        var outputPath = Path.Combine(_tempDirectory, "nested", "TestResults.xml");
        var response = new RunTestsCommand.RunTestsResponse
        {
            Outcome = outcome,
            JunitBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(junit))
        };

        var exitCode = RunTestsCommand.MaterializeResult(response, outputPath);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(junit, File.ReadAllText(outputPath));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("PG5vdC14bWw+")]
    public void MaterializeResultRejectsMalformedJUnit(string junitBase64)
    {
        var outputPath = Path.Combine(_tempDirectory, "TestResults.xml");
        var response = new RunTestsCommand.RunTestsResponse
        {
            Outcome = "passed",
            JunitBase64 = junitBase64
        };

        var exitCode = RunTestsCommand.MaterializeResult(response, outputPath);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void MaterializeResultReturnsInfrastructureExitWithoutWritingJUnit()
    {
        var outputPath = Path.Combine(_tempDirectory, "TestResults.xml");
        var response = new RunTestsCommand.RunTestsResponse
        {
            Outcome = "infrastructureFailure",
            JunitBase64 = null
        };

        var exitCode = RunTestsCommand.MaterializeResult(response, outputPath);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("passed", "<testsuite tests=\"1\" failures=\"1\" errors=\"0\"><testcase name=\"Red\"><failure /></testcase></testsuite>")]
    [InlineData("failed", "<testsuite tests=\"1\" failures=\"0\" errors=\"0\"><testcase name=\"Green\" /></testsuite>")]
    public void MaterializeResultRejectsOutcomeThatDisagreesWithJUnit(string outcome, string junit)
    {
        var outputPath = Path.Combine(_tempDirectory, "TestResults.xml");
        var response = new RunTestsCommand.RunTestsResponse
        {
            Outcome = outcome,
            JunitBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(junit))
        };

        var exitCode = RunTestsCommand.MaterializeResult(response, outputPath);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void MaterializeResultReturnsInfrastructureExitForUnwritableDestination()
    {
        Directory.CreateDirectory(_tempDirectory);
        var blockingFile = Path.Combine(_tempDirectory, "not-a-directory");
        File.WriteAllText(blockingFile, "block");
        var outputPath = Path.Combine(blockingFile, "TestResults.xml");
        var junit = "<testsuite tests=\"1\" failures=\"0\" errors=\"0\"><testcase name=\"Green\" /></testsuite>";
        var response = new RunTestsCommand.RunTestsResponse
        {
            Outcome = "passed",
            JunitBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(junit))
        };

        var exitCode = RunTestsCommand.MaterializeResult(response, outputPath, out var error);

        Assert.Equal(2, exitCode);
        Assert.StartsWith("Could not write JUnit", error);
        Assert.False(File.Exists(outputPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}