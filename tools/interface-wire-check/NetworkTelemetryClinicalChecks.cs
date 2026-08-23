using MSUIClient;
using MSUIClient.Net;

internal static class NetworkTelemetryClinicalChecks
{
    public static void Run()
    {
        Check(NetworkClient.PingIntervalMs == 30_000 &&
              NetworkClient.RttHistoryDepth == 16,
            "reference ping cadence or RTT ring depth drift");

        string source = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Net", "NetworkClient.cs"));
        Check(source.Contains("_rttHistory.Count == RttHistoryDepth", StringComparison.Ordinal) &&
              source.Contains("_rttHistory.Dequeue();", StringComparison.Ordinal) &&
              source.Contains("sum / _rttHistory.Count", StringComparison.Ordinal),
            "latency meter no longer averages the bounded RTT ring");
        Check(source.Contains("Volatile.Write(ref _lastRttMs, sample);", StringComparison.Ordinal) &&
              source.Contains("Volatile.Read(ref _lastRttMs)", StringComparison.Ordinal),
            "CMSG_PING no longer reports the most recent RTT separately from the UI average");
        Check(source.Contains("null, PingIntervalMs, PingIntervalMs", StringComparison.Ordinal) &&
              !source.Contains("null, 0, 10_000", StringComparison.Ordinal),
            "keepalive timer is not the verified delayed 30-second cadence");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
