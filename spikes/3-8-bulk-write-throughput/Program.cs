using BulkWriteThroughputSpike;

// See README.md for the required env vars, the full command list, and — most importantly — the
// open question this story flags for Ralf (DTU tier bump / low-usage window) before running any
// command against the real Azure SQL Basic instance.

var providerEnv = Environment.GetEnvironmentVariable("SPIKE_PROVIDER");
var connectionString = Environment.GetEnvironmentVariable("SPIKE_CONNECTION_STRING");

if (string.IsNullOrWhiteSpace(providerEnv) || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set SPIKE_PROVIDER=postgres|sqlserver and SPIKE_CONNECTION_STRING before running. See README.md.");
    return 1;
}

var provider = providerEnv.Trim().ToLowerInvariant() switch
{
    "postgres" => SpikeProvider.Postgres,
    "sqlserver" => SpikeProvider.SqlServer,
    _ => throw new ArgumentException($"Unknown SPIKE_PROVIDER '{providerEnv}' — expected 'postgres' or 'sqlserver'."),
};

var command = args.Length > 0 ? args[0] : "run-all";
var cancelAfterMsArg = args
    .SkipWhile(a => a != "--cancel-after-ms")
    .Skip(1)
    .FirstOrDefault();

await using var db = new SpikeDbContext(provider, connectionString);
var providerName = provider.ToString();

switch (command)
{
    case "setup":
        await Scenarios.CreateSchemaAsync(db, provider);
        break;

    case "teardown":
        await Scenarios.DropSchemaAsync(db, provider);
        await Scenarios.AssertNoSpikeObjectsAsync(db, provider);
        break;

    case "verify-clean":
        await Scenarios.AssertNoSpikeObjectsAsync(db, provider);
        break;

    case "truncate":
        await Scenarios.TruncateReadingsAsync(db, provider);
        break;

    case "ac4":
        await Scenarios.Ac4InsertEmptyAsync(db, providerName);
        break;

    case "preload":
        await Scenarios.PreloadAsync(db, providerName);
        break;

    case "ac5":
        var (ac5PowerPointId, ac5Batch) = await Scenarios.Ac5InsertIntoPreloadedAsync(db, providerName);
        SpikeState.SaveAc5State(ac5PowerPointId, ac5Batch.Take(250).Select(r => r.IntervalStart).ToList());
        break;

    case "ac6":
        var ac5State = SpikeState.LoadAc5State();
        // AC #6a's exact resubmitted batch isn't persisted (120k rows) — re-generate it
        // deterministically (same seed as AC #5's GenerateSingleDeviceBatch) so the resubmission
        // targets the same (PowerPointId, IntervalStart) keys AC #5 actually inserted.
        var regenerated = DataGenerator.GenerateSingleDeviceBatch(
            Scenarios.HouseholdId, ac5State.PowerPointId, smartPlugImportId: null, Scenarios.AnchorEnd).ToList();
        await Scenarios.Ac6aResubmitFullOverlapAsync(db, providerName, regenerated);
        await Scenarios.Ac6bResubmitIncrementalDeltaAsync(db, providerName, ac5State.PowerPointId, ac5State.SampleIntervalStarts);
        break;

    case "ac7":
        await Scenarios.Ac7NullPowerPointAsync(db, providerName);
        break;

    case "ac8":
        var cancelAfterMs = cancelAfterMsArg is not null ? int.Parse(cancelAfterMsArg) : 500;
        await Scenarios.Ac8CancellationRollbackAsync(db, providerName, cancelAfterMs);
        break;

    case "run-all":
        await RunAllAsync(db, provider, providerName, cancelAfterMsArg);
        break;

    default:
        Console.Error.WriteLine($"Unknown command '{command}'. See README.md for the full list.");
        return 1;
}

return 0;

static async Task RunAllAsync(SpikeDbContext db, SpikeProvider provider, string providerName, string? cancelAfterMsArg)
{
    Console.WriteLine($"=== run-all: {providerName} ===");

    // A scenario throwing (a genuine finding, e.g. AC #7's match-key check — see README.md
    // "Findings log") must never skip teardown: this story leaves no trace in either database
    // (AC #10) regardless of whether every scenario succeeded. Each scenario is isolated so one
    // failure doesn't stop the rest from running and being measured.
    async Task<StepResult<T>> RunStep<T>(string label, Func<Task<T>> step)
    {
        try
        {
            var value = await step();
            return new StepResult<T> { Success = true, Value = value };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SCENARIO '{label}' THREW — recording as a finding, continuing: {ex.GetType().Name}: {ex.Message}");
            ResultsLog.Record(providerName, $"{label}-EXCEPTION", 0, 0, ex.Message);
            return new StepResult<T> { Success = false };
        }
    }

    try
    {
        await Scenarios.CreateSchemaAsync(db, provider);

        var ac4Result = await RunStep("AC4", () => Scenarios.Ac4InsertEmptyAsync(db, providerName));

        await Scenarios.TruncateReadingsAsync(db, provider);
        await RunStep("preload", () => Scenarios.PreloadAsync(db, providerName));

        var ac5Result = await RunStep("AC5", () => Scenarios.Ac5InsertIntoPreloadedAsync(db, providerName));
        if (ac5Result.Success)
        {
            var ac5 = ac5Result.Value;
            await RunStep<object?>("AC6a", async () =>
            {
                await Scenarios.Ac6aResubmitFullOverlapAsync(db, providerName, ac5.Batch);
                return null;
            });

            var sampleIntervalStarts = ac5.Batch.Take(250).Select(r => r.IntervalStart).ToList();
            await RunStep<object?>("AC6b", async () =>
            {
                await Scenarios.Ac6bResubmitIncrementalDeltaAsync(db, providerName, ac5.PowerPointId, sampleIntervalStarts);
                return null;
            });
        }
        else
        {
            Console.WriteLine("Skipping AC6a/AC6b — AC #5 did not succeed, no batch to resubmit.");
        }

        await RunStep<object?>("AC7", async () =>
        {
            await Scenarios.Ac7NullPowerPointAsync(db, providerName);
            return null;
        });

        await Scenarios.TruncateReadingsAsync(db, provider);
        var cancelAfterMs = cancelAfterMsArg is not null
            ? int.Parse(cancelAfterMsArg)
            : Math.Max(50, (int)((ac4Result.Success ? ac4Result.Value : 2000) * 0.2));
        Console.WriteLine($"AC #8 cancellation timing: {cancelAfterMs} ms (20% of AC #4's measured elapsed, unless overridden).");
        await RunStep<object?>("AC8", async () =>
        {
            await Scenarios.Ac8CancellationRollbackAsync(db, providerName, cancelAfterMs);
            return null;
        });
    }
    finally
    {
        await Scenarios.DropSchemaAsync(db, provider);
        await Scenarios.AssertNoSpikeObjectsAsync(db, provider);
    }

    Console.WriteLine($"=== run-all complete: {providerName} — see results-log.csv ===");
}

// A reference-type wrapper, not a bare `T?` return — an earlier version returned `T?` directly
// from an unconstrained generic method and relied on `default`/pattern-matching to detect
// failure. That silently did NOT behave as "no value" for a value-type T (confirmed the hard way:
// after a real AC #5 failure against Azure SQL, AC #6a still ran against a zeroed-out/null batch
// and threw its own separate, misleading "Value cannot be null (Parameter 'source')" exception).
// An explicit `Success` flag on a class sidesteps the whole unconstrained-nullable-value-type
// question — this is guaranteed correct regardless of what T is.
sealed class StepResult<T>
{
    public required bool Success { get; init; }
    // Only meaningful when Success is true — Success is the source of truth for "is there a
    // value", not this property's own nullability (see the class-level comment above on why).
    public T Value { get; init; } = default!;
}
