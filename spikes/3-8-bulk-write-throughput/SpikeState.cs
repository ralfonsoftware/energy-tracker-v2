using System.Text.Json;

namespace BulkWriteThroughputSpike;

// Tiny local scratch file so AC #5's PowerPointId/IntervalStart sample can be handed to a later,
// separately-invoked `ac6` run — needed when running scenario-by-scenario against Azure SQL
// (Dev Notes' DTU caution) rather than via `run-all`. Not a durable artifact; safe to delete
// between spike sessions.
public static class SpikeState
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "spike-state.json");

    public record Ac5State(Guid PowerPointId, List<DateTimeOffset> SampleIntervalStarts);

    public static void SaveAc5State(Guid powerPointId, List<DateTimeOffset> sampleIntervalStarts)
    {
        var state = new Ac5State(powerPointId, sampleIntervalStarts);
        File.WriteAllText(System.IO.Path.GetFullPath(Path), JsonSerializer.Serialize(state));
    }

    public static Ac5State LoadAc5State()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException("No spike-state.json found — run 'ac5' before 'ac6'.");
        }
        return JsonSerializer.Deserialize<Ac5State>(File.ReadAllText(fullPath))
               ?? throw new InvalidOperationException("spike-state.json was empty/invalid.");
    }
}
