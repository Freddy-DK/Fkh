using Xunit;

namespace Fkh.Backend.UnitTests;

public class BruteForceTests
{
    // Each test uses a unique IP so the shared static dictionary cannot cross-contaminate.
    private static string NewIp() => $"10.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 255)}-{Guid.NewGuid():N}";

    [Fact]
    public void Ip_is_not_blocked_before_any_failures()
    {
        Assert.False(FunctionBase.IsIpBlocked(NewIp()));
    }

    [Fact]
    public void Ip_is_not_blocked_below_threshold()
    {
        var ip = NewIp();
        FunctionBase.RecordFailedAttempt(ip);
        FunctionBase.RecordFailedAttempt(ip);
        Assert.False(FunctionBase.IsIpBlocked(ip));
    }

    [Fact]
    public void Ip_is_blocked_at_threshold()
    {
        var ip = NewIp();
        FunctionBase.RecordFailedAttempt(ip);
        FunctionBase.RecordFailedAttempt(ip);
        FunctionBase.RecordFailedAttempt(ip);
        Assert.True(FunctionBase.IsIpBlocked(ip));
        Assert.Contains(ip, FunctionBase.GetBlockedIps());
    }

    [Fact]
    public void Clearing_attempts_unblocks_the_ip()
    {
        var ip = NewIp();
        FunctionBase.RecordFailedAttempt(ip);
        FunctionBase.RecordFailedAttempt(ip);
        FunctionBase.RecordFailedAttempt(ip);
        Assert.True(FunctionBase.IsIpBlocked(ip));

        FunctionBase.ClearFailedAttempts(ip);

        Assert.False(FunctionBase.IsIpBlocked(ip));
        Assert.DoesNotContain(ip, FunctionBase.GetBlockedIps());
    }
}
