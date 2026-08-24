namespace Fkh.E2ETests;

// Timestamped logging written straight to the console so it is visible live in the CI run log
// (and local `dotnet run`) for every test, not only failed ones.
internal static class E2ELog
{
    public static void Line(string message)
        => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
}
