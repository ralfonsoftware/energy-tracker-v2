namespace EnergyTracker.Application;

/// <summary>The largest export file a restore may read into memory on this process (Story 11.25, AD-24); derived from the memory the runtime reports, so a bigger container raises it and a smaller one lowers it.</summary>
public sealed record RestoreMemoryBudget(long MaxExportBytes)
{
    // The API's resident set when idle, and the extra peak per byte of export file (docs/data-import-restore.md,
    // "Memory under a 1 GiB container"). Measured at 1.9 for a real 131 MiB export and 2.6 for a 32 MiB
    // generated one; 3.0 keeps the budget on the safe side of both, because the shape of a larger export is unknown.
    public const long IdleBytes = 170L * 1024 * 1024;

    public const double PeakBytesPerExportByte = 3.0;

    public static RestoreMemoryBudget Unbounded { get; } = new(long.MaxValue);

    // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is already the heap limit under a container limit
    // (75 % of it, so 768 MiB in a 1 GiB container) and physical memory without one.
    public static RestoreMemoryBudget FromAvailableMemory(long totalAvailableMemoryBytes)
    {
        var usable = totalAvailableMemoryBytes - IdleBytes;
        return new(usable <= 0 ? 0 : (long)(usable / PeakBytesPerExportByte));
    }
}
