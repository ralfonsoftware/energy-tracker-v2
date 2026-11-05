using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RestoreMemoryBudgetTests
{
    private const long MiB = 1024L * 1024;

    // What the runtime reports as available inside a 1 GiB container: its 75 % heap limit.
    private const long OneGiBContainerReportsBytes = 768 * MiB;

    // The 131 MiB household export of Story 11.25's measurement (a real household, September 2026).
    private const long MeasuredHouseholdExportBytes = 137_375_117;

    // The upload cap (HouseholdImportEndpoints.MaxFileSizeBytes): a file of this size cannot restore in 1 GiB.
    private const long UploadCapBytes = 250 * MiB;

    [Fact]
    public void A_one_GiB_container_admits_the_measured_household_export_but_not_a_file_at_the_upload_cap()
    {
        var budget = RestoreMemoryBudget.FromAvailableMemory(OneGiBContainerReportsBytes);

        budget.MaxExportBytes.ShouldBeGreaterThanOrEqualTo(MeasuredHouseholdExportBytes);
        budget.MaxExportBytes.ShouldBeLessThan(UploadCapBytes);
    }

    [Fact]
    public void A_larger_container_admits_a_larger_file()
    {
        RestoreMemoryBudget.FromAvailableMemory(2 * OneGiBContainerReportsBytes).MaxExportBytes
            .ShouldBeGreaterThan(RestoreMemoryBudget.FromAvailableMemory(OneGiBContainerReportsBytes).MaxExportBytes);
    }

    [Fact]
    public void Memory_below_what_the_process_needs_when_idle_admits_no_file()
    {
        RestoreMemoryBudget.FromAvailableMemory(RestoreMemoryBudget.IdleBytes).MaxExportBytes.ShouldBe(0);
        RestoreMemoryBudget.FromAvailableMemory(0).MaxExportBytes.ShouldBe(0);
    }

    [Fact]
    public void The_unbounded_budget_admits_any_file()
    {
        RestoreMemoryBudget.Unbounded.MaxExportBytes.ShouldBe(long.MaxValue);
    }
}
