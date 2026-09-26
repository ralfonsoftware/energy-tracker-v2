using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-3: HouseholdExportReader's keyset-paginated reads (spec-household-export-oom-fix.md) must
// stay behind the global HouseholdId query filter (or an explicit HouseholdId match, for
// HouseholdMember which has no filter) like every other query in this adapter — never
// IgnoreQueryFilters/FromSqlRaw/FromSqlInterpolated, and never DbSet<T>.Find (which also bypasses
// the filter). A source-text scan rather than a runtime/reflection check, matching this project's
// existing guard-test convention (see PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests).
public class HouseholdExportReaderDoesNotBypassTenantIsolationTests
{
    private static readonly string[] ForbiddenIdentifiers = ["IgnoreQueryFilters", "FromSqlRaw", "FromSqlInterpolated", ".Find("];

    private const string RelativeFilePath = "src/EnergyTracker.Infrastructure/Adapters/HouseholdExportReader.cs";

    [Fact]
    public void HouseholdExportReader_never_bypasses_the_AD3_tenant_isolation_filter()
    {
        var repoRoot = FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        // Strips a trailing `//` line comment (this file has no string literals containing "//",
        // so a naive first-index search is safe) rather than only skipping whole-line comments —
        // otherwise a legitimate code line with an explanatory trailing comment that happens to
        // name a forbidden identifier (e.g. "// never IgnoreQueryFilters here") would false-positive.
        var codeLines = File.ReadAllLines(fullPath)
            .Select(line =>
            {
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                return commentIndex >= 0 ? line[..commentIndex] : line;
            });

        foreach (var line in codeLines)
        {
            foreach (var identifier in ForbiddenIdentifiers)
            {
                line.Contains(identifier, StringComparison.Ordinal)
                    .ShouldBeFalse($"{RelativeFilePath} references forbidden identifier '{identifier}' (AD-3): {line}");
            }
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
