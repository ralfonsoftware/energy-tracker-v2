using System.Globalization;

namespace BulkWriteThroughputSpike;

// Append-only raw measurement log (AC #9's own throughput table is written from this once every
// scenario has run on both providers). Deliberately CSV, not the final markdown deliverable —
// that's assembled afterwards at _bmad-artifacts/implementation/spike-results/
// 3-8-bulk-write-throughput-spike-results.md once every row below exists for both providers.
public static class ResultsLog
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results-log.csv");

    public static void Record(string provider, string scenario, int rowCount, double elapsedMs, string notes = "")
    {
        var path = Path.GetFullPath(LogPath);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "TimestampUtc,Provider,Scenario,RowCount,ElapsedMs,RowsPerSec,Notes\n");
        }

        var rowsPerSec = elapsedMs > 0 ? rowCount / (elapsedMs / 1000.0) : 0;
        var line = string.Join(",",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            provider,
            scenario,
            rowCount.ToString(CultureInfo.InvariantCulture),
            elapsedMs.ToString("F1", CultureInfo.InvariantCulture),
            rowsPerSec.ToString("F1", CultureInfo.InvariantCulture),
            $"\"{notes.Replace("\"", "'")}\"");

        File.AppendAllText(path, line + "\n");

        Console.WriteLine(
            $"[{provider}] {scenario}: {rowCount:N0} rows in {elapsedMs:N0} ms " +
            $"({rowsPerSec:N0} rows/sec){(notes.Length > 0 ? $" — {notes}" : "")}");
    }
}
