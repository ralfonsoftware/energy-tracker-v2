using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.19 (audit R1): the "caller has no Household" guard is one endpoint filter, RequireHousehold(),
// not a private copy per endpoint file. A source-text scan (comments stripped), the same convention as
// NoDirectClockReadsTests, so a later endpoint file cannot quietly bring the copy back.
public class HouseholdGuardIsOneEndpointFilterTests
{
    private const string EndpointsFolder = "src/EnergyTracker.Api/Endpoints";

    private static readonly string[] Forbidden =
    [
        "TryGetHouseholdId",
        "NoHouseholdDetail",
        "does not belong to a Household",
    ];

    [Fact]
    public void No_endpoint_file_defines_its_own_household_guard()
    {
        var root = FindRepoRoot();
        var hits = EndpointFiles(root)
            .SelectMany(f => HitsIn(CSharpSource.StripComments(File.ReadAllText(Path.Combine(root, f)))).Select(h => $"{f} ({h})"))
            .ToList();

        hits.ShouldBeEmpty(
            "an endpoint file repeats the Household guard: map its routes on api.MapGroup(string.Empty).RequireHousehold() instead (Story 11.19, audit R1). "
            + string.Join("; ", hits));
    }

    [Fact]
    public void The_filter_still_owns_the_rejection_text()
    {
        var code = CSharpSource.StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), "src/EnergyTracker.Api/HouseholdRequiredFilter.cs")));

        code.ShouldContain("does not belong to a Household", Case.Sensitive, "HouseholdRequiredFilter.cs no longer holds the 403 text: was it moved? Update this guard with it.");
    }

    [Fact]
    public void The_scan_covers_the_endpoint_files()
    {
        EndpointFiles(FindRepoRoot()).Count.ShouldBeGreaterThan(10, "the scan found almost no endpoint files: did the layout move? Update this guard with it.");
    }

    // Non-vacuous: the matcher is proven against known-bad text, so a refactor of this test cannot
    // silently turn it into one that scans nothing.
    [Fact]
    public void The_scan_itself_detects_what_it_guards_against_and_ignores_comments()
    {
        const string bad = """
            private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";
            private static bool TryGetHouseholdId(ICurrentHouseholdAccessor a, out Guid id) { id = default; return false; }
            """;
        HitsIn(CSharpSource.StripComments(bad)).ShouldBe(["TryGetHouseholdId", "NoHouseholdDetail", "does not belong to a Household"]);

        HitsIn(CSharpSource.StripComments("// TryGetHouseholdId copied rather than referenced\nvar x = 1;")).ShouldBeEmpty();
        HitsIn(CSharpSource.StripComments("var household = api.MapGroup(string.Empty).RequireHousehold();")).ShouldBeEmpty();
    }

    private static List<string> HitsIn(string code) =>
        Forbidden.Where(token => code.Contains(token, StringComparison.Ordinal)).ToList();

    private static List<string> EndpointFiles(string root)
    {
        var folder = Path.Combine(root, EndpointsFolder);
        Directory.Exists(folder).ShouldBeTrue($"{EndpointsFolder} does not exist: did the layout move? Update this guard with it.");
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find EnergyTracker.sln above the test binaries.");
    }
}
