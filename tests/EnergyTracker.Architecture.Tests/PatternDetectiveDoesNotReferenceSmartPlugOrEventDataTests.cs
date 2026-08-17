using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-14: MeterReading is the sole authoritative total — no SmartPlugReading/Event data may be
// read, summed, or referenced anywhere in Story 2.4's Pattern Detective calculation/service/DTO
// code. Neither SmartPlugReading nor Event exists in the codebase yet (Epic 3/Epic 6), so this
// guard is a source-text scan rather than a type check — it exists so a *future* story can't
// quietly wire either type into this code path once they do exist.
public class PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests
{
    private static readonly string[] ForbiddenIdentifiers = ["SmartPlugReading", "Event"];

    private static readonly string[] RelativeFilePaths =
    [
        "src/EnergyTracker.Domain/Calculations/BonusDecayNormalizer.cs",
        "src/EnergyTracker.Domain/Calculations/PatternDetectiveCalculator.cs",
        "src/EnergyTracker.Domain/Status.cs",
        "src/EnergyTracker.Domain/StatusSnapshot.cs",
        "src/EnergyTracker.Application/GetCurrentStatus.cs",
        "src/EnergyTracker.Application/Ports/IStatusRecomputeService.cs",
        "src/EnergyTracker.Infrastructure/Adapters/StatusRecomputeService.cs",
        "src/EnergyTracker.Infrastructure/Configurations/StatusSnapshotConfiguration.cs",
        "src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs",
    ];

    [Fact]
    public void No_Pattern_Detective_source_file_mentions_SmartPlugReading_or_Event()
    {
        var repoRoot = FindRepoRoot();

        foreach (var relativePath in RelativeFilePaths)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

            var codeLines = File.ReadAllLines(fullPath)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

            foreach (var line in codeLines)
            {
                foreach (var identifier in ForbiddenIdentifiers)
                {
                    System.Text.RegularExpressions.Regex.IsMatch(line, $@"\b{identifier}\b")
                        .ShouldBeFalse($"{relativePath} references forbidden identifier '{identifier}' (AD-14): {line}");
                }
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
