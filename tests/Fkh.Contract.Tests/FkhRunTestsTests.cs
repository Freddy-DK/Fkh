using System.Text;
using Fkh;
using Fkh.Services;
using Xunit;

namespace Fkh.Contract.Tests;

public sealed class FkhRunTestsTests
{
    [Fact]
    public void RunTestsUsesAuthenticatedFunctionPipeline()
    {
        Assert.Equal(typeof(FunctionBase), typeof(RunTestsFunction).BaseType);
        var function = Assert.Single(FunctionCatalog.Functions, definition => definition.Name == "RunTests");
        Assert.Equal("RunTests", function.Route);
        Assert.Collection(
            function.Parameters,
            parameter => Assert.Equal("name", parameter.Name),
            parameter => Assert.Equal("tenant", parameter.Name),
            parameter => Assert.Equal("extensionId", parameter.Name),
            parameter => Assert.Equal("appName", parameter.Name),
            parameter => Assert.Equal("testCodeunitRange", parameter.Name),
            parameter => Assert.Equal("timeoutMinutes", parameter.Name));
    }

    [Fact]
    public void ParseJUnitDerivesPassingCounts()
    {
        var junit = Encoding.UTF8.GetBytes("<testsuite tests=\"2\" failures=\"0\" errors=\"0\" skipped=\"1\" time=\"1.25\"><testcase name=\"One\" /><testcase name=\"Two\"><skipped /></testcase></testsuite>");

        var result = FkhRunTests.ParseJUnit(junit);

        Assert.Equal("passed", result.Outcome);
        Assert.Equal(2, result.Tests);
        Assert.Equal(0, result.Failures);
        Assert.Equal(0, result.Errors);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1.25, result.DurationSeconds);
    }

    [Fact]
    public void ParseJUnitDerivesFailureFromXml()
    {
        var junit = Encoding.UTF8.GetBytes("<testsuites tests=\"2\" failures=\"1\" errors=\"1\" skipped=\"0\" time=\"2.5\"><testsuite name=\"Tests\"><testcase name=\"Red\"><failure /></testcase><testcase name=\"Error\"><error /></testcase></testsuite></testsuites>");

        var result = FkhRunTests.ParseJUnit(junit);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(2, result.Tests);
        Assert.Equal(1, result.Failures);
        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public void ParseJUnitAggregatesBcContainerHelperSuites()
    {
        var junit = Encoding.UTF8.GetBytes("<testsuites><testsuite name=\"One\" tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"0.25\"><testcase name=\"Green\" /></testsuite><testsuite name=\"Two\" tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\" time=\"0.5\"><testcase name=\"Red\"><failure /></testcase></testsuite></testsuites>");

        var result = FkhRunTests.ParseJUnit(junit);

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(2, result.Tests);
        Assert.Equal(1, result.Failures);
        Assert.Equal(0.75, result.DurationSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<not-junit />")]
    [InlineData("<testsuite tests=\"0\" failures=\"0\" errors=\"0\" />")]
    public void ParseJUnitRejectsUntrustworthyResults(string junit)
    {
        Assert.Throws<InvalidOperationException>(() => FkhRunTests.ParseJUnit(Encoding.UTF8.GetBytes(junit)));
    }

    [Fact]
    public void ParseJUnitRejectsCountsThatDoNotMatchTestCases()
    {
        var junit = Encoding.UTF8.GetBytes("<testsuite tests=\"2\" failures=\"0\" errors=\"0\"><testcase name=\"OnlyOne\" /></testsuite>");

        Assert.Throws<InvalidOperationException>(() => FkhRunTests.ParseJUnit(junit));
    }

    [Theory]
    [InlineData("default", "11111111-1111-1111-1111-111111111111")]
    [InlineData("tenant-2", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")]
    public void ValidateParametersAcceptsSafeTenantAndGuid(string tenant, string extensionId)
    {
        var request = FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["tenant"] = tenant,
            ["extensionId"] = extensionId
        });

        Assert.Equal(tenant, request.Tenant);
        Assert.Equal(Guid.Parse(extensionId), request.ExtensionId);
    }

    [Theory]
    [InlineData("default'; Remove-Item C:\\*")]
    [InlineData("../default")]
    [InlineData("")]
    public void ValidateParametersRejectsUnsafeTenant(string tenant)
    {
        Assert.Throws<InvalidOperationException>(() => FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["tenant"] = tenant,
            ["extensionId"] = "11111111-1111-1111-1111-111111111111"
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ValidateParametersRejectsInvalidExtensionId(string extensionId)
    {
        Assert.Throws<InvalidOperationException>(() => FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["tenant"] = "default",
            ["extensionId"] = extensionId
        }));
    }

    [Fact]
    public void ValidateParametersDefaultsTimeoutMinutes()
    {
        var request = FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["extensionId"] = "11111111-1111-1111-1111-111111111111"
        });

        Assert.Equal(30, request.TimeoutMinutes);
    }

    [Fact]
    public void ValidateParametersAcceptsTimeoutMinutes()
    {
        var request = FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["extensionId"] = "11111111-1111-1111-1111-111111111111",
            ["timeoutMinutes"] = "90"
        });

        Assert.Equal(90, request.TimeoutMinutes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void ValidateParametersRejectsInvalidTimeoutMinutes(string timeoutMinutes)
    {
        Assert.Throws<InvalidOperationException>(() => FkhRunTests.ValidateParameters(new Dictionary<string, string>
        {
            ["extensionId"] = "11111111-1111-1111-1111-111111111111",
            ["timeoutMinutes"] = timeoutMinutes
        }));
    }
}