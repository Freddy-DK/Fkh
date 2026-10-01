using System.Diagnostics;
using System.Text.Json;
using Fkh.Services;
using Xunit;

namespace Fkh.Backend.UnitTests;

public class TelemetryTests
{
    [Theory]
    [InlineData("https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/26.1.41234.0/us", "sandbox", "26.1.41234.0", "26", "us")]
    [InlineData("https://bcartifacts.blob.core.windows.net/onprem/25.0.23364.0/w1", "onprem", "25.0.23364.0", "25", "w1")]
    [InlineData("https://bcinsider.blob.core.windows.net/sandbox/27.0.1.0/DK?sv=2021&sig=secret", "sandbox", "27.0.1.0", "27", "dk")]
    public void Parse_extracts_public_artifact_details(string url, string type, string version, string major, string country)
    {
        var info = ArtifactInfo.Parse(url);
        Assert.Equal(new ArtifactInfo(type, version, major, country), info);
    }

    [Theory]
    [InlineData("https://mystorage.blob.core.windows.net/artifacts/sandbox/26.0.0.0/us?sv=2021&sig=secret")]
    [InlineData("https://contoso.example.com/bc/26.0/us")]
    public void Parse_reports_private_artifacts_as_custom(string url)
    {
        Assert.Equal(new ArtifactInfo("custom", null, null, null), ArtifactInfo.Parse(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Parse_handles_missing_or_invalid_urls(string? url)
    {
        Assert.Equal("unknown", ArtifactInfo.Parse(url).Type);
    }

    [Fact]
    public void Hash_is_deterministic_case_insensitive_and_salt_dependent()
    {
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt-a", (_, _) => Task.CompletedTask);
        var a1 = FkhTelemetry.UserHash("FreddyDK");
        var a2 = FkhTelemetry.UserHash("freddydk");

        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt-b", (_, _) => Task.CompletedTask);
        var b = FkhTelemetry.UserHash("freddydk");

        Assert.NotNull(a1);
        Assert.Equal(16, a1!.Length);
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.DoesNotContain("freddy", a1, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_and_container_hashes_differ_for_same_value()
    {
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt", (_, _) => Task.CompletedTask);
        Assert.NotEqual(FkhTelemetry.UserHash("demo"), FkhTelemetry.ContainerHash("demo"));
    }

    [Fact]
    public void Payload_contains_envelope_and_omits_null_properties()
    {
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep-123", "salt", (_, _) => Task.CompletedTask);
        var json = FkhTelemetry.BuildPayload("FunctionCall", new Dictionary<string, object?>
        {
            ["operation"] = "ListContainers",
            ["missing"] = null,
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("FunctionCall", root.GetProperty("name").GetString());
        Assert.Equal("dep-123", root.GetProperty("deploymentId").GetString());
        Assert.Equal(FkhTelemetry.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        var props = root.GetProperty("properties");
        Assert.Equal("ListContainers", props.GetProperty("operation").GetString());
        Assert.False(props.TryGetProperty("missing", out _));
    }

    [Fact]
    public void Track_returns_immediately_even_when_endpoint_hangs()
    {
        var release = new TaskCompletionSource();
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt", (_, _) => release.Task);

        var stopwatch = Stopwatch.StartNew();
        FkhTelemetry.Track("FunctionCall", new Dictionary<string, object?> { ["operation"] = "x" });
        stopwatch.Stop();

        release.SetResult();
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Track took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Track_drops_events_when_too_many_are_in_flight()
    {
        var release = new TaskCompletionSource();
        var sent = 0;
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt", (_, _) =>
        {
            Interlocked.Increment(ref sent);
            return release.Task;
        }, maxInFlight: 2);

        for (var i = 0; i < 5; i++)
            FkhTelemetry.Track("FunctionCall");

        release.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task Track_ignores_send_failures()
    {
        FkhTelemetry.ConfigureForTests("https://usage.example/api", "dep", "salt", (_, _) => throw new HttpRequestException("down"));
        FkhTelemetry.Track("FunctionCall");
        FkhTelemetry.Track("FunctionCall");
        await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Track_does_nothing_when_not_configured()
    {
        var sent = 0;
        FkhTelemetry.ConfigureForTests(null, null, null, (_, _) => { sent++; return Task.CompletedTask; });
        FkhTelemetry.Track("FunctionCall");
        Assert.Equal(0, sent);
        Assert.Null(FkhTelemetry.UserHash("someone"));
    }
}
