using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-25 (Story 11.1, audit S1/S12): access to a Household is granted only by the server-side
// invite flow, never by data. CurrentHouseholdAccessor resolves the Household from the caller's
// (ExternalIssuer, ExternalSubjectId), so a restore that wrote HouseholdMember rows from an
// uploaded file could grant arbitrary identities access or lock every member out, and an export
// that carried those identities would hand the raw material to any member. Three executable
// guards: the restore payload cannot carry members, the export member entry is exactly
// { Id, DisplayName }, and the restore writer's code never touches the HouseholdMembers set.
public class HouseholdRestoreNeverCarriesAccessGrantsTests
{
    private const string RestoreWriterRelativePath = "src/EnergyTracker.Infrastructure/Adapters/HouseholdRestoreWriter.cs";

    [Fact]
    public void HouseholdRestoreData_has_no_property_that_carries_HouseholdMember_entities()
    {
        var offenders = typeof(HouseholdRestoreData).GetProperties()
            .Where(p => p.PropertyType.ToString().Contains("HouseholdMember", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        offenders.ShouldBeEmpty("HouseholdRestoreData must never carry HouseholdMember rows (AD-25).");
    }

    [Fact]
    public void HouseholdMemberExportDto_exposes_exactly_Id_and_DisplayName()
    {
        // The exported member entry must never carry a login identity (AD-25, audit S12).
        typeof(HouseholdMemberExportDto).GetProperties().Select(p => p.Name).Order().ShouldBe(["DisplayName", "Id"]);
    }

    [Fact]
    public void HouseholdRestoreWriter_never_references_the_HouseholdMembers_set_in_code()
    {
        var fullPath = Path.Combine(FindRepoRoot(), RestoreWriterRelativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        // Strips a trailing `//` comment (this file has no string literals containing "//"), so the
        // explanatory comments that legitimately name HouseholdMember (why it is untouched) don't
        // false-positive. Same approach as HouseholdExportReaderDoesNotBypassTenantIsolationTests.
        var codeLines = File.ReadAllLines(fullPath)
            .Select(line =>
            {
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                return commentIndex >= 0 ? line[..commentIndex] : line;
            });

        foreach (var line in codeLines)
        {
            // "HouseholdMember" (no trailing s) also catches Set<HouseholdMember>() and the type name.
            line.Contains("HouseholdMember", StringComparison.Ordinal)
                .ShouldBeFalse($"{RestoreWriterRelativePath} must not read or write HouseholdMember rows (AD-25): {line}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
        }

        return dir.FullName;
    }
}
