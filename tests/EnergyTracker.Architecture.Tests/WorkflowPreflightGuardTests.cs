using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.5 (AC #5, #7): each Azure-authenticated workflow starts with a fail-fast
// `Preflight: identity permissions` step that runs scripts/ci/azure-preflight.sh in the mode for
// that workflow, and that step must come before every step that mutates something (migrations,
// image push, deploy, what-if on a PR). A missing or excess right then fails the run with a clear
// message before anything changes. Workflows are scanned as text — no YAML library, matching the
// sibling MigrationDeployWorkflowRestorePointGuardTests — with whole-line `#` comments stripped
// first so explanatory comments can neither satisfy nor break an assertion.
public class WorkflowPreflightGuardTests
{
    private const string PreflightStepName = "Preflight: identity permissions";
    private const string PreflightScript = "scripts/ci/azure-preflight.sh";

    private static readonly Regex StepStart = new(@"^\s*- name:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    // Job keys sit at two-space indent directly under `jobs:`.
    private static readonly Regex JobStart = new(@"^  (?<id>[A-Za-z0-9_-]+):\s*$", RegexOptions.Compiled);

    [Fact]
    public void App_deploy_runs_the_deploy_app_preflight_after_the_firewall_rule_and_before_every_mutating_step()
    {
        var steps = ReadSteps(".github/workflows/app-deploy.yml", job: "deploy");

        AssertPreflight(
            ".github/workflows/app-deploy.yml (job deploy)",
            steps,
            mode: "deploy-app",
            mustPrecede:
            [
                "Apply pending EF Core migrations",
                "Build and push image",
                "Ensure ACR pull credential and target port",
                "Deploy new revision",
            ]);

        // The production-database read probe needs the run-scoped firewall rule that this step
        // creates, so the preflight cannot move ahead of it.
        steps.FindIndex(s => s.Name == "Whitelist runner IP for migration")
            .ShouldBeLessThan(
                steps.FindIndex(s => s.Name == PreflightStepName),
                "app-deploy.yml: the preflight's database read needs the firewall rule, so it must run after " +
                "'Whitelist runner IP for migration'.");
    }

    [Fact]
    public void Infra_deploy_runs_the_deploy_infra_preflight_before_the_deploy_step()
    {
        var steps = ReadSteps(".github/workflows/infra-deploy.yml", job: null);

        AssertPreflight(
            ".github/workflows/infra-deploy.yml",
            steps,
            mode: "deploy-infra",
            mustPrecede: ["Deploy"]);
    }

    [Fact]
    public void Acr_retention_runs_the_retention_preflight_before_the_prune_step()
    {
        var steps = ReadSteps(".github/workflows/acr-retention.yml", job: null);

        AssertPreflight(
            ".github/workflows/acr-retention.yml",
            steps,
            mode: "retention",
            mustPrecede:
            [
                "Check the running image survives the purge",
                "Prune old images (keep newest 7)",
            ]);
    }

    [Fact]
    public void Pr_review_validate_infra_runs_the_pr_preflight_before_the_what_if()
    {
        var steps = ReadSteps(".github/workflows/pr-review.yml", job: "validate-infra");

        AssertPreflight(
            ".github/workflows/pr-review.yml (job validate-infra)",
            steps,
            mode: "pr",
            mustPrecede: ["What-if validation"],
            allowCondition: true);
    }

    private static void AssertPreflight(string where, List<Step> steps, string mode, string[] mustPrecede, bool allowCondition = false)
    {
        var preflightIndex = steps.FindIndex(s => s.Name == PreflightStepName);
        preflightIndex.ShouldBeGreaterThanOrEqualTo(
            0,
            $"{where} has no step named '{PreflightStepName}'. Add it right after the Azure login " +
            $"(it runs {PreflightScript} {mode}) so a missing or excess right fails before anything changes.");

        var invocation = new Regex($@"{Regex.Escape(PreflightScript)}\s+{Regex.Escape(mode)}\b");
        invocation.IsMatch(steps[preflightIndex].Body).ShouldBeTrue(
            $"{where}: step '{PreflightStepName}' must run `{PreflightScript} {mode}`.");

        // A preflight that can be skipped or that is allowed to fail without failing the job is not
        // fail-fast. pr-review.yml's preflight legitimately carries the same `if:` as the Azure
        // steps around it (fork PRs and PRs without infra changes get no Azure login at all).
        Regex.IsMatch(steps[preflightIndex].Body, @"^\s*continue-on-error:", RegexOptions.Multiline).ShouldBeFalse(
            $"{where}: step '{PreflightStepName}' must not set continue-on-error; a failed preflight has to fail the job.");
        if (!allowCondition)
        {
            Regex.IsMatch(steps[preflightIndex].Body, @"^\s*if:", RegexOptions.Multiline).ShouldBeFalse(
                $"{where}: step '{PreflightStepName}' must not have an `if:`; it has to run on every deploy.");
        }

        foreach (var name in mustPrecede)
        {
            var index = steps.FindIndex(s => s.Name == name);
            index.ShouldBeGreaterThanOrEqualTo(
                0,
                $"{where}: step '{name}' not found. If it was renamed, update this guard together with it.");
            preflightIndex.ShouldBeLessThan(
                index,
                $"{where}: step '{PreflightStepName}' must come BEFORE '{name}' (AC #5) — a permission " +
                "problem found after the step has run is the late failure the preflight exists to prevent.");
        }
    }

    private sealed record Step(string Name, string Body);

    // job == null scans the whole file (single-job workflows); otherwise only the lines of that job.
    private static List<Step> ReadSteps(string relativePath, string? job)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        var lines = StripWholeLineComments(File.ReadAllText(fullPath)).Split('\n').ToList();
        if (job is not null)
        {
            lines = ScopeToJob(lines, job, relativePath);
        }

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

    private static List<string> ScopeToJob(List<string> lines, string job, string relativePath)
    {
        var jobsIndex = lines.FindIndex(l => l.TrimEnd() == "jobs:");
        jobsIndex.ShouldBeGreaterThanOrEqualTo(0, $"No top-level `jobs:` in {relativePath}.");

        var start = -1;
        for (var i = jobsIndex + 1; i < lines.Count; i++)
        {
            var match = JobStart.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            if (start >= 0)
            {
                return lines.GetRange(start, i - start);
            }

            if (match.Groups["id"].Value == job)
            {
                start = i;
            }
        }

        start.ShouldBeGreaterThanOrEqualTo(0, $"Job '{job}' not found in {relativePath}.");
        return lines.GetRange(start, lines.Count - start);
    }

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
