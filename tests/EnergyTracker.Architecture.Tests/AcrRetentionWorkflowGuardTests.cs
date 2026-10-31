using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// ACR image retention (spec-acr-image-retention): acr-retention.yml deletes registry images, and
// deleted image data is unrecoverable. These guards pin what keeps that safe: it only runs on its
// schedule or by hand, never on a push or pull request; the preflight and the running-image check
// come before the prune; a failed run fails the workflow; and every `acr purge` is scoped to the
// energy-tracker repository, age-bound, and keeps the 7 newest tags. The workflow is scanned as
// text — no YAML library, matching the sibling guards — with whole-line `#` comments stripped first
// so explanatory comments can neither satisfy nor break an assertion.
public class AcrRetentionWorkflowGuardTests
{
    private const string RelativeFilePath = ".github/workflows/acr-retention.yml";
    private const string PreflightStepName = "Preflight: identity permissions";
    private const string RunningImageStepName = "Check the running image survives the purge";
    private const string PruneStepName = "Prune old images (keep newest 7)";

    private static readonly Regex StepStart = new(@"^\s*- name:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    private static readonly Regex PurgeCommand = new(@"acr purge\b[^\n]*", RegexOptions.Compiled);

    // `--ago 0d` for the tag purge, `1h` or longer for the sweeps (hours, or days above zero).
    private static readonly Regex AgoValue = new(@"--ago\s+(?<n>\d+)(?<unit>[dhm])\b", RegexOptions.Compiled);

    [Fact]
    public void Workflow_runs_weekly_on_sunday_at_01_00_utc_or_by_hand_and_never_on_push_or_pull_request()
    {
        var onBlock = ReadTopLevelBlock("on");

        onBlock.ShouldContain(
            "cron: '0 1 * * 0'",
            customMessage: $"{RelativeFilePath} must run weekly, Sunday 01:00 UTC (cron '0 1 * * 0').");
        onBlock.ShouldContain("workflow_dispatch:", customMessage: $"{RelativeFilePath} must allow a manual (dry) run.");
        Regex.IsMatch(onBlock, @"^\s*(push|pull_request|pull_request_target|workflow_run):", RegexOptions.Multiline).ShouldBeFalse(
            $"{RelativeFilePath} deletes registry images; it must only run on its schedule or by hand, never on a push or pull request.");
    }

    [Fact]
    public void Preflight_and_the_running_image_check_run_before_the_prune_and_none_of_them_may_fail_silently()
    {
        var steps = ReadSteps();
        var preflight = FindStep(steps, PreflightStepName);
        var runningImage = FindStep(steps, RunningImageStepName);
        var prune = FindStep(steps, PruneStepName);

        Regex.IsMatch(preflight.Body, @"scripts/ci/azure-preflight\.sh\s+retention\b").ShouldBeTrue(
            $"{RelativeFilePath}: step '{PreflightStepName}' must run scripts/ci/azure-preflight.sh retention.");

        steps.IndexOf(preflight).ShouldBeLessThan(
            steps.IndexOf(runningImage),
            $"{RelativeFilePath}: '{PreflightStepName}' must come before '{RunningImageStepName}'.");
        steps.IndexOf(runningImage).ShouldBeLessThan(
            steps.IndexOf(prune),
            $"{RelativeFilePath}: '{RunningImageStepName}' must come before '{PruneStepName}'; purging the image " +
            "the Container App runs would break its next cold start.");

        runningImage.Body.ShouldContain("--top 7", customMessage: "The running-image check must compare against the same 7 newest tags the purge keeps.");
        runningImage.Body.ShouldContain("exit 1", customMessage: "The running-image check must stop the run when the running tag would be purged.");
        runningImage.Body.ShouldContain(
            "/energy-tracker@",
            customMessage: "The running-image check must fail closed for a digest-pinned image; it cannot be compared with the newest tags.");

        foreach (var step in new[] { preflight, runningImage, prune })
        {
            Regex.IsMatch(step.Body, @"^\s*(continue-on-error|if):", RegexOptions.Multiline).ShouldBeFalse(
                $"{RelativeFilePath}: step '{step.Name}' must not carry `continue-on-error` or `if:`; a failed " +
                "check or purge has to fail the run so GitHub notifies.");
        }
    }

    [Fact]
    public void Every_purge_is_scoped_to_the_energy_tracker_repository_age_bound_and_keeps_the_seven_newest_tags()
    {
        var prune = FindStep(ReadSteps(), PruneStepName);
        var purges = PurgeCommand.Matches(prune.Body).Select(m => m.Value).ToList();

        purges.Count.ShouldBeGreaterThanOrEqualTo(
            4,
            $"{RelativeFilePath}: expected one tag purge and three orphan sweeps; the sweeps repeat because manifests " +
            "are freed only after the manifest list that references them is gone.");

        foreach (var purge in purges)
        {
            purge.ShouldContain("--filter 'energy-tracker:", customMessage: $"Purge not scoped to the energy-tracker repository: {purge}");
            purge.ShouldNotContain("--include-locked", customMessage: $"Purge must not override image locks: {purge}");
            AgoValue.IsMatch(purge).ShouldBeTrue($"Purge without --ago: {purge}");
        }

        var tagPurges = purges.Where(p => !p.Contains("--untagged-only", StringComparison.Ordinal)).ToList();
        tagPurges.Count.ShouldBe(1, "Exactly one purge may delete tags.");
        Regex.IsMatch(tagPurges[0], @"--keep\s+7\b").ShouldBeTrue($"The tag purge must keep the 7 newest tags: {tagPurges[0]}");
        tagPurges[0].ShouldNotContain("--untagged ", customMessage: "The tag purge must not also delete untagged manifests; the sweeps do that after the lists are gone.");

        var sweeps = purges.Where(p => p.Contains("--untagged-only", StringComparison.Ordinal)).ToList();
        sweeps.Count.ShouldBeGreaterThanOrEqualTo(3, "Expected at least three orphan sweeps.");
        foreach (var sweep in sweeps)
        {
            var ago = AgoValue.Match(sweep);
            var atLeastAnHour = ago.Success && (
                (ago.Groups["unit"].Value == "h" && int.Parse(ago.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) >= 1) ||
                (ago.Groups["unit"].Value == "d" && int.Parse(ago.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) >= 1));
            atLeastAnHour.ShouldBeTrue(
                $"An orphan sweep must skip manifests younger than an hour (a push in flight): {sweep}");
        }
    }

    [Fact]
    public void Dry_run_input_reaches_every_purge_command()
    {
        var prune = FindStep(ReadSteps(), PruneStepName);

        prune.Body.ShouldContain("inputs.dry_run", customMessage: "The prune step must read the dry_run input.");
        var purges = PurgeCommand.Matches(prune.Body).Select(m => m.Value).ToList();
        foreach (var purge in purges)
        {
            purge.ShouldContain("${DRY_RUN_FLAG}", customMessage: $"Purge ignores the dry-run flag: {purge}");
        }
    }

    [Fact]
    public void Workflow_does_not_share_the_deploy_concurrency_group()
    {
        // A concurrency group keeps one pending run, so a purge queued behind a deploy in
        // `infra-app-deploy` could replace a queued deploy.
        var text = StripWholeLineComments(ReadWorkflow());

        text.ShouldContain("group: acr-retention");
        text.ShouldNotContain("infra-app-deploy");
    }

    private sealed record Step(string Name, string Body);

    private static Step FindStep(List<Step> steps, string name)
    {
        var step = steps.FirstOrDefault(s => s.Name == name);
        step.ShouldNotBeNull($"Step '{name}' not found in {RelativeFilePath}.");
        return step;
    }

    // The lines under a top-level key (no indent) up to the next top-level key.
    private static string ReadTopLevelBlock(string key)
    {
        var lines = StripWholeLineComments(ReadWorkflow()).Split('\n');
        var block = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^[A-Za-z_]"))
            {
                inside = line.StartsWith(key + ":", StringComparison.Ordinal);
                continue;
            }

            if (inside)
            {
                block.Add(line);
            }
        }

        block.ShouldNotBeEmpty($"Top-level key '{key}' not found in {RelativeFilePath}.");
        return string.Join('\n', block);
    }

    private static List<Step> ReadSteps()
    {
        var lines = StripWholeLineComments(ReadWorkflow()).Split('\n');
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

    private static string ReadWorkflow()
    {
        var fullPath = Path.Combine(FindRepoRoot(), RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        return File.ReadAllText(fullPath);
    }

    // Drops whole-line "#" comments (after trimming leading whitespace) so explanatory comments in
    // the workflow — which mention `acr purge` and the other group name — are not scanned.
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
