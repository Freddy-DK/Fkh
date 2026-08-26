using System.Security.Cryptography;

namespace Fkh.E2ETests;

// Streaming file helpers for round-trip tests: never buffer large files in memory.
internal static class E2EFiles
{
    // Writes a random file of the given size, returning its SHA-256.
    public static string WriteRandomFile(string path, long sizeBytes)
    {
        const int chunk = 8 * 1024 * 1024;
        var buffer = new byte[chunk];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, chunk, FileOptions.SequentialScan);
        long written = 0;
        while (written < sizeBytes)
        {
            Random.Shared.NextBytes(buffer);
            var toWrite = (int)Math.Min(chunk, sizeBytes - written);
            fs.Write(buffer, 0, toWrite);
            hash.AppendData(buffer, 0, toWrite);
            written += toWrite;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
