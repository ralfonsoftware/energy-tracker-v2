using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 10.1 (migration safety on deploy): app-deploy.yml's "Apply pending EF Core migrations"
// step must record a PITR restore point in $GITHUB_STEP_SUMMARY *before* its first
// `dotnet ef database update`, and that step must run before "Deploy new revision". Without the
// marker, a bad migration has no recorded point to restore to (infra/README.md, "Rolling back a
// bad migration"). The workflow is scanned as text — no YAML library is referenced, matching the
// dependency-light sibling guards — and whole-line `#` comments are stripped first (same
// precedent as the sibling guards) so the explanatory comments in the workflow can neither
// satisfy nor break an assertion.
public class MigrationDeployWorkflowRestorePointGuardTests
{
    private const string RelativeFilePath = ".github/workflows/app-deploy.yml";
    private const string MigrationStepName = "Apply pending EF Core migrations";
    private const string DeployStepName = "Deploy new revision";
    private const string SummaryVariable = "GITHUB_STEP_SUMMARY";
    private const string EfUpdateCommand = "dotnet ef database update";

    private static readonly Regex StepStart = new(@"^\s*- name:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    [Fact]
    public void Migration_step_writes_the_restore_point_summary_before_running_dotnet_ef_database_update()
    {
        var steps = ReadSteps();
        var migrationStep = FindStep(steps, MigrationStepName);

        var summaryIndex = migrationStep.Body.IndexOf(SummaryVariable, StringComparison.Ordinal);
        summaryIndex.ShouldBeGreaterThanOrEqualTo(
            0,
            $"Step '{MigrationStepName}' in {RelativeFilePath} must write the pre-migration restore point to " +
            $"${SummaryVariable} (AC #4a) — the block is missing.");

        var efUpdateIndex = migrationStep.Body.IndexOf(EfUpdateCommand, StringComparison.Ordinal);
        efUpdateIndex.ShouldBeGreaterThanOrEqualTo(
            0,
            $"Step '{MigrationStepName}' in {RelativeFilePath} no longer contains '{EfUpdateCommand}' — " +
            "if migrations moved, this guard needs to move with them.");

        summaryIndex.ShouldBeLessThan(
            efUpdateIndex,
            $"Step '{MigrationStepName}' in {RelativeFilePath} writes ${SummaryVariable} AFTER '{EfUpdateCommand}' " +
            "(AC #4b) — the restore point must be recorded before the first migration attempt.");
    }

    [Fact]
    public void Migration_step_precedes_the_deploy_new_revision_step()
    {
        var steps = ReadSteps();
        var migrationIndex = steps.FindIndex(s => s.Name == MigrationStepName);
        var deployIndex = steps.FindIndex(s => s.Name == DeployStepName);

        migrationIndex.ShouldBeGreaterThanOrEqualTo(0, $"Step '{MigrationStepName}' not found in {RelativeFilePath}.");
        deployIndex.ShouldBeGreaterThanOrEqualTo(0, $"Step '{DeployStepName}' not found in {RelativeFilePath}.");
        migrationIndex.ShouldBeLessThan(
            deployIndex,
            $"Step '{MigrationStepName}' must run before '{DeployStepName}' in {RelativeFilePath} (AC #4c) — " +
            "otherwise new code ships against an un-migrated schema.");
    }

    private sealed record Step(string Name, string Body);

    private static Step FindStep(List<Step> steps, string name)
    {
        var step = steps.FirstOrDefault(s => s.Name == name);
        step.ShouldNotBeNull($"Step '{name}' not found in {RelativeFilePath}.");
        return step;
    }

    private static List<Step> ReadSteps()
    {
        var fullPath = Path.Combine(FindRepoRoot(), RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        var lines = StripWholeLineComments(File.ReadAllText(fullPath)).Split('\n');
        var steps = new List<Step>();
        string? currentName = null;
        var body = new List<string>();

        foreach (var line in lines)
        {
            var match = StepStart.Match(line);
            if (match.Success)
            {
                if (currentName is not null)
                {
                    steps.Add(new Step(currentName, string.Join('\n', body)));
                }

                currentName = match.Groups["name"].Value.Trim('"', '\'');
                body.Clear();
            }
            else if (currentName is not null)
            {
                body.Add(line);
            }
        }

        if (currentName is not null)
        {
            steps.Add(new Step(currentName, string.Join('\n', body)));
        }

        return steps;
    }

    // Drops whole-line "#" comments (after trimming leading whitespace) so explanatory comments in
    // the workflow — which mention GITHUB_STEP_SUMMARY and `dotnet ef` by name — are not scanned.
    private static string StripWholeLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith('#') ? string.Empty : line));

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
