using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Fkh.Services;

public class FkhRunTests : FkhServiceBase
{
    private const int MaxJUnitBytes = 10 * 1024 * 1024;
    private const int MaxDiagnosticCharacters = 32 * 1024;
    private const int DefaultTimeoutMinutes = 30;
    private const int MaxTimeoutMinutes = 120;
    private const string JUnitMarker = "FKH_JUNIT_BASE64:";
    private static readonly Regex TenantPattern = new("^[A-Za-z0-9][A-Za-z0-9-]{0,127}$", RegexOptions.CultureInvariant);

    public FkhRunTests(ILogger<FkhRunTests> logger) : base(logger) { }

    public async Task<object> RunTestsAsync(Dictionary<string, string> parameters)
    {
        var request = ValidateParameters(parameters);
        var appName = ResolveAppName(parameters);

        Logger.LogInformation(
            "User '{User}' running extension '{ExtensionId}' tests in container '{Container}'.",
            parameters["_githubUsername"], request.ExtensionId, appName);

        var client = await GetKubernetesClientAsync();
        var pods = await client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={appName}");
        var pod = pods.Items.FirstOrDefault(IsReady)
            ?? throw new InvalidOperationException($"No ready container found for '{appName}'. Make sure the container is started and ready.");

        var requestBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Tenant,
            ExtensionId = request.ExtensionId.ToString(),
            request.AppName,
            request.TestCodeunitRange,
            request.TimeoutMinutes
        }));
        var script = $"& 'C:\\run\\my\\Run-FkhBcTests.ps1' -RequestBase64 '{requestBase64}'";
        var detachedResult = await RunDetachedInBcPodAsync(
            client,
            pod.Metadata.Name,
            pod.Spec.Containers[0].Name,
            jobPrefix: "fkh-runtests",
            jobIdInput: $"{appName}|{request.Tenant}|{request.ExtensionId}|{request.AppName}|{request.TestCodeunitRange}|{request.TimeoutMinutes}",
            script: script,
            retryAfterSeconds: 5,
            retryMessage: "Tests still running...");

        if (!string.IsNullOrWhiteSpace(detachedResult.Stderr))
            throw new InvalidOperationException($"Test execution failed in container '{appName}': {BoundDiagnostic(detachedResult.Stderr)}");

        var (junitBytes, log) = ExtractResult(detachedResult.Stdout);
        var result = ParseJUnit(junitBytes);
        return new
        {
            result.Outcome,
            result.Tests,
            result.Failures,
            result.Errors,
            result.Skipped,
            result.DurationSeconds,
            JunitBase64 = Convert.ToBase64String(junitBytes),
            Log = log
        };
    }

    internal static RunTestsRequest ValidateParameters(Dictionary<string, string> parameters)
    {
        var tenant = parameters.TryGetValue("tenant", out var tenantValue)
            ? tenantValue
            : "default";
        if (!TenantPattern.IsMatch(tenant))
            throw new InvalidOperationException("Tenant must contain only letters, digits, and hyphens.");

        if (!parameters.TryGetValue("extensionId", out var extensionIdValue)
            || !Guid.TryParse(extensionIdValue, out var extensionId)
            || extensionId == Guid.Empty)
            throw new InvalidOperationException("extensionId must be a non-empty GUID.");

        parameters.TryGetValue("appName", out var appName);
        if (appName?.IndexOfAny(['\r', '\n']) >= 0 || appName?.Length > 250)
            throw new InvalidOperationException("appName is invalid.");

        parameters.TryGetValue("testCodeunitRange", out var testCodeunitRange);
        testCodeunitRange = testCodeunitRange?.Trim();
        if (testCodeunitRange is not null
            && (testCodeunitRange.Length == 0 || testCodeunitRange.IndexOfAny(['\r', '\n']) >= 0 || testCodeunitRange.Length > 250))
            throw new InvalidOperationException("testCodeunitRange is invalid.");

        var timeoutMinutes = DefaultTimeoutMinutes;
        if (parameters.TryGetValue("timeoutMinutes", out var timeoutValue) && !string.IsNullOrWhiteSpace(timeoutValue))
        {
            if (!int.TryParse(timeoutValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out timeoutMinutes)
                || timeoutMinutes < 1 || timeoutMinutes > MaxTimeoutMinutes)
                throw new InvalidOperationException($"timeoutMinutes must be a whole number between 1 and {MaxTimeoutMinutes}.");
        }

        return new RunTestsRequest(tenant, extensionId, appName, testCodeunitRange, timeoutMinutes);
    }

    internal static RunTestsResult ParseJUnit(byte[] junitBytes)
    {
        if (junitBytes.Length == 0)
            throw new InvalidOperationException("Test execution returned empty JUnit.");

        XDocument document;
        try
        {
            using var stream = new MemoryStream(junitBytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("Test execution returned malformed JUnit.", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName is not ("testsuite" or "testsuites"))
            throw new InvalidOperationException("Test execution returned an unsupported JUnit document.");

        var testCases = root.DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "testcase")
            .ToList();
        if (testCases.Count == 0)
            throw new InvalidOperationException("No tests matched the requested extension ID.");

        var failures = testCases.Count(testCase => testCase.Elements().Any(element => element.Name.LocalName == "failure"));
        var errors = testCases.Count(testCase => testCase.Elements().Any(element => element.Name.LocalName == "error"));
        var skipped = testCases.Count(testCase => testCase.Elements().Any(element => element.Name.LocalName == "skipped"));
        var summaryElements = GetSummaryElements(root);
        var declaredTests = summaryElements.Sum(element => GetRequiredCount(element, "tests"));
        var declaredFailures = summaryElements.Sum(element => GetOptionalCount(element, "failures"));
        var declaredErrors = summaryElements.Sum(element => GetOptionalCount(element, "errors"));
        var declaredSkipped = summaryElements.Sum(element => GetOptionalCount(element, "skipped"));

        if (declaredTests != testCases.Count
            || declaredFailures != failures
            || declaredErrors != errors
            || declaredSkipped != skipped)
            throw new InvalidOperationException("JUnit summary counts do not match its test cases.");

        var durationSeconds = summaryElements.Sum(GetOptionalDuration);
        return new RunTestsResult(
            failures + errors == 0 ? "passed" : "failed",
            testCases.Count,
            failures,
            errors,
            skipped,
            durationSeconds);
    }

    private static bool IsReady(V1Pod pod)
        => string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase)
            && pod.Status?.ContainerStatuses?.Count > 0
            && pod.Status.ContainerStatuses.All(status => status.Ready);

    private static (byte[] JUnitBytes, string[] Log) ExtractResult(string stdout)
    {
        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var marker = lines.LastOrDefault(line => line.StartsWith(JUnitMarker, StringComparison.Ordinal));
        if (marker is null)
            throw new InvalidOperationException("Test execution did not return JUnit.");
        if (marker.Length - JUnitMarker.Length > ((MaxJUnitBytes + 2) / 3) * 4)
            throw new InvalidOperationException($"Test execution returned JUnit larger than {MaxJUnitBytes} bytes.");

        byte[] junitBytes;
        try
        {
            junitBytes = Convert.FromBase64String(marker[JUnitMarker.Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Test execution returned invalid JUnit encoding.", ex);
        }

        if (junitBytes.Length > MaxJUnitBytes)
            throw new InvalidOperationException($"Test execution returned JUnit larger than {MaxJUnitBytes} bytes.");

        var log = lines
            .Where(line => !line.StartsWith(JUnitMarker, StringComparison.Ordinal))
            .TakeLast(200)
            .Select(BoundDiagnostic)
            .ToArray();
        return (junitBytes, log);
    }

    private static string BoundDiagnostic(string value)
        => value.Length <= MaxDiagnosticCharacters
            ? value
            : $"[truncated] {value[^MaxDiagnosticCharacters..]}";

    private static IReadOnlyList<XElement> GetSummaryElements(XElement root)
    {
        if (root.Name.LocalName == "testsuite" || root.Attribute("tests") is not null)
            return [root];

        var suites = root.Elements()
            .Where(element => element.Name.LocalName == "testsuite")
            .ToList();
        if (suites.Count == 0)
            throw new InvalidOperationException("JUnit contains no test suite summaries.");
        return suites;
    }

    private static int GetRequiredCount(XElement root, string attributeName)
    {
        var value = root.Attribute(attributeName)?.Value;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new InvalidOperationException($"JUnit is missing a valid {attributeName} count.");
        return count;
    }

    private static int GetOptionalCount(XElement root, string attributeName)
    {
        var value = root.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new InvalidOperationException($"JUnit contains an invalid {attributeName} count.");
        return count;
    }

    private static double GetOptionalDuration(XElement root)
    {
        var value = root.Attribute("time")?.Value;
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration < 0)
            throw new InvalidOperationException("JUnit contains an invalid duration.");
        return duration;
    }

    internal sealed record RunTestsRequest(string Tenant, Guid ExtensionId, string? AppName, string? TestCodeunitRange, int TimeoutMinutes);
    internal sealed record RunTestsResult(string Outcome, int Tests, int Failures, int Errors, int Skipped, double DurationSeconds);
}