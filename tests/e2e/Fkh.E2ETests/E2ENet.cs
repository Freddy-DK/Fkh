using System.Net.Sockets;

namespace Fkh.E2ETests;

// Network probes for the access-gating tests: check whether a TCP port is reachable.
internal static class E2ENet
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Attempts a single TCP connection; true if it connects within the probe timeout.
    public static bool IsTcpOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(ProbeTimeout);
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // Polls until the port becomes reachable, or the budget elapses.
    public static bool WaitUntilOpen(string host, int port, TimeSpan budget)
        => Poll(() => IsTcpOpen(host, port), expectOpen: true, budget);

    // Polls until the port becomes unreachable, or the budget elapses.
    public static bool WaitUntilClosed(string host, int port, TimeSpan budget)
        => Poll(() => IsTcpOpen(host, port), expectOpen: false, budget);

    private static bool Poll(Func<bool> isOpen, bool expectOpen, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            if (isOpen() == expectOpen) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(PollInterval);
        }
    }
}
