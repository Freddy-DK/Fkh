using System.Diagnostics;
using Xunit;

namespace Fkh.E2ETests;

// Base class for E2E tests: logs a clear start/result marker for each test so the CI run log
// shows which test is executing and how it finished.
public abstract class E2ETest : IDisposable
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly string _name;

    protected E2ETest()
    {
        _name = TestContext.Current.Test?.TestDisplayName ?? GetType().Name;
        E2ELog.Line($">>> RUNNING  {_name}");
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        var result = TestContext.Current.TestState?.Result.ToString() ?? "Finished";
        E2ELog.Line($"<<< {result.ToUpperInvariant()}  {_name}  ({_stopwatch.Elapsed:hh\\:mm\\:ss})");
        GC.SuppressFinalize(this);
    }
}
