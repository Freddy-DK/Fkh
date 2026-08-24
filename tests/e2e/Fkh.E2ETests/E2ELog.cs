using Xunit;

namespace Fkh.E2ETests;

// Timestamped logging that routes to the current test's output (visible in test results and on
// failure) or to the console when no test context is active (e.g. the assembly fixture).
internal static class E2ELog
{
    public static void Line(string message)
    {
        var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        var helper = TestContext.Current?.TestOutputHelper;
        if (helper is not null)
            helper.WriteLine(stamped);
        else
            Console.WriteLine(stamped);
    }
}
