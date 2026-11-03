using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.24 (migration timeout headroom): app-deploy.yml's "Apply pending EF Core migrations"
// step must keep a per-command `Command Timeout` large enough for a long SQL Server column
// rewrite on Basic-tier Azure SQL, a step cap that fits it, and a retry loop that does not retry
// a command timeout (a timeout is not a firewall problem; five attempts cannot fit the cap).
// Same text-scan approach as MigrationDeployWorkflowRestorePointGuardTests: no YAML library,
// whole-line `#` comments stripped so the explanatory comments cannot satisfy or break an
// assertion. The rule and the baseline live in project-context.md, "Migrations".
public class MigrationDeployWorkflowTimeoutGuardTests
{
    private const string RelativeFilePath = ".github/workflows/app-deploy.yml";
    private const string MigrationStepName = "Apply pending EF Core migrations";
    private const string EfUpdateCommand = "dotnet ef database update";
    private const string PreflightStepName = "Preflight: identity permissions";
    private const string TimeoutMessage = "Execution Timeout Expired";
    private const string TimeoutDetectLine = @"if grep -qF ""Execution Timeout Expired"" ""$EF_UPDATE_OUT""; then";
    private const string RetrySleep = "sleep 30";

    private const int ExpectedCommandTimeoutSeconds = 1800;
    private const int ExpectedStepTimeoutMinutes = 60;

    // Floor for the step cap: one full command timeout, plus the work the step does besides
    // migrating (migrations list capped at 120 s, a build allowance), plus the 4 sleeps between
    // attempts, plus a margin for rollback of the timed-out statement. Margins approved by Ralf.
    private const int MigrationsListSeconds = 120;
    private const int BuildAllowanceSeconds = 300;
    private const int SleepSecondsBetweenAttempts = 4 * 30;
    private const int RollbackMarginSeconds = 600;

    // Floor for the deploy job cap: the preflight cap plus the migration step cap plus the other
    // steps (four 5-minute firewall/lookup caps, image push, deploy, a 5-minute health check).
    private const int OtherDeployStepsAllowanceMinutes = 35;

    private static readonly Regex StepStart = new(@"^\s*- name:\s*(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex StepTimeoutMinutes = new(@"^\s*timeout-minutes:\s*(?<n>\d+)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DeployJobTimeoutMinutes = new(@"^  deploy:[ \t]*\n(?:(?:    .*)?\n)*?    timeout-minutes:[ \t]*(?<n>\d+)[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ConnectionString = new(@"CONNECTION_STRING=""[^""]*""", RegexOptions.Compiled);
    private static readonly Regex CommandTimeout = new(@"(?:^|;)Command Timeout=(?<n>\d+);", RegexOptions.Compiled);

    [Fact]
    public void Migration_step_pins_the_command_timeout_step_cap_and_timeout_detection()
    {
        Violations(ReadWorkflowText()).ShouldBeEmpty();
    }

    [Fact]
    public void Known_bad_workflow_without_command_timeout_fails_the_guard()
    {
        var bad = Mutate("Command Timeout=1800;", string.Empty);

        Violations(bad).ShouldContain(v => v.Contains("Command Timeout"));
    }

    [Fact]
    public void Known_bad_workflow_with_lowered_command_timeout_fails_the_guard()
    {
        var bad = Mutate("Command Timeout=1800;", "Command Timeout=600;");

        Violations(bad).ShouldContain(v => v.Contains("Command Timeout"));
    }

    [Fact]
    public void Known_bad_workflow_with_connection_timeout_regressed_fails_the_guard()
    {
        var bad = Mutate("Connection Timeout=30;", string.Empty);

        Violations(bad).ShouldContain(v => v.Contains("Connection Timeout"));
    }

    [Fact]
    public void Known_bad_workflow_with_step_cap_below_the_floor_fails_the_guard()
    {
        var bad = Regex.Replace(
            ReadWorkflowText(),
            @"(- name: Apply pending EF Core migrations\s*\n\s*timeout-minutes:) \d+",
            "$1 20");

        Violations(bad).ShouldContain(v => v.Contains("below the floor"));
    }

    [Fact]
    public void Known_bad_workflow_with_job_cap_below_the_floor_fails_the_guard()
    {
        var bad = DeployJobTimeoutMinutes.Replace(
            ReadWorkflowText(),
            m => m.Value[..(m.Groups["n"].Index - m.Index)] + "30");

        Violations(bad).ShouldContain(v => v.Contains("job timeout-minutes"));
    }

    [Fact]
    public void Known_bad_workflow_that_retries_a_command_timeout_fails_the_guard()
    {
        var bad = Mutate(TimeoutMessage, "something else");

        Violations(bad).ShouldContain(v => v.Contains(TimeoutMessage));
    }

    [Fact]
    public void Known_bad_workflow_that_detects_a_timeout_but_does_not_exit_fails_the_guard()
    {
        var bad = Regex.Replace(
            ReadWorkflowText(),
            @"(" + Regex.Escape(TimeoutDetectLine) + @"\n[^\n]*\n)[ \t]*exit 1\n",
            "$1");

        bad.ShouldNotBe(ReadWorkflowText(), "Known-bad sample precondition: the exit 1 after the timeout check was not removed.");
        Violations(bad).ShouldContain(v => v.Contains(TimeoutMessage));
    }

    private static List<string> Violations(string workflowText)
    {
        var violations = new List<string>();
        var step = ReadSteps(workflowText).FirstOrDefault(s => s.Name == MigrationStepName);
        if (step is null)
        {
            violations.Add($"Step '{MigrationStepName}' not found in {RelativeFilePath}.");
            return violations;
        }

        var connectionString = ConnectionString.Match(step.Body);
        if (!connectionString.Success)
        {
            violations.Add($"Step '{MigrationStepName}' has no CONNECTION_STRING assignment.");
            return violations;
        }

        if (!connectionString.Value.Contains("Connection Timeout=30;", StringComparison.Ordinal))
        {
            violations.Add("CONNECTION_STRING must keep 'Connection Timeout=30;' (time to connect).");
        }

        var command = CommandTimeout.Match(connectionString.Value);
        var commandSeconds = command.Success ? int.Parse(command.Groups["n"].Value, CultureInfo.InvariantCulture) : -1;
        if (commandSeconds != ExpectedCommandTimeoutSeconds)
        {
            violations.Add(
                $"CONNECTION_STRING must carry 'Command Timeout={ExpectedCommandTimeoutSeconds};' (found {(command.Success ? commandSeconds.ToString(CultureInfo.InvariantCulture) : "none")}).");
        }

        var minutes = StepTimeoutMinutes.Match(step.Body);
        var stepMinutes = minutes.Success ? int.Parse(minutes.Groups["n"].Value, CultureInfo.InvariantCulture) : -1;
        if (stepMinutes != ExpectedStepTimeoutMinutes)
        {
            violations.Add($"Step timeout-minutes must be {ExpectedStepTimeoutMinutes} (found {(minutes.Success ? stepMinutes.ToString(CultureInfo.InvariantCulture) : "none")}).");
        }

        // The floor uses the parsed Command Timeout, so the relation holds for whatever value ships.
        var floorCommandSeconds = Math.Max(commandSeconds, ExpectedCommandTimeoutSeconds);
        var floorSeconds = floorCommandSeconds + MigrationsListSeconds + BuildAllowanceSeconds
            + SleepSecondsBetweenAttempts + RollbackMarginSeconds;
        if (stepMinutes * 60 < floorSeconds)
        {
            violations.Add($"Step timeout-minutes ({stepMinutes}) x 60 is below the floor of {floorSeconds} s (Command Timeout + list + build + sleeps + rollback margin).");
        }

        var preflight = ReadSteps(workflowText).FirstOrDefault(s => s.Name == PreflightStepName);
        var preflightMatch = preflight is null ? Match.Empty : StepTimeoutMinutes.Match(preflight.Body);
        var job = DeployJobTimeoutMinutes.Match(workflowText);
        if (!preflightMatch.Success || !job.Success)
        {
            violations.Add($"Deploy job timeout-minutes and the '{PreflightStepName}' step's timeout-minutes must both be set.");
        }
        else
        {
            var preflightMinutes = int.Parse(preflightMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
            var jobMinutes = int.Parse(job.Groups["n"].Value, CultureInfo.InvariantCulture);
            var jobFloorMinutes = preflightMinutes + Math.Max(stepMinutes, ExpectedStepTimeoutMinutes) + OtherDeployStepsAllowanceMinutes;
            if (jobMinutes < jobFloorMinutes)
            {
                violations.Add($"Deploy job timeout-minutes ({jobMinutes}) is below the floor of {jobFloorMinutes} (preflight + migration step + other steps).");
            }
        }

        violations.AddRange(TimeoutDetectionViolations(step.Body));

        return violations;
    }

    private static IEnumerable<string> TimeoutDetectionViolations(string stepBody)
    {
        var lines = stepBody.Split('\n').Select(l => l.Trim()).ToList();
        var updateLine = lines.FindIndex(l => l.Contains(EfUpdateCommand, StringComparison.Ordinal));
        var detectLine = lines.FindIndex(l => l == TimeoutDetectLine);
        var sleepLine = lines.FindIndex(l => l == RetrySleep);
        var fiLine = detectLine < 0 ? -1 : lines.FindIndex(detectLine, l => l == "fi");
        var exits = fiLine >= 0 && lines.Skip(detectLine + 1).Take(fiLine - detectLine - 1).Contains("exit 1");

        if (updateLine < 0 || detectLine < updateLine || sleepLine < detectLine || !exits)
        {
            yield return $"The retry loop must run '{TimeoutDetectLine}' followed by 'exit 1' after '{EfUpdateCommand}' and before '{RetrySleep}' ({TimeoutMessage} is not retried).";
        }
    }

    private static string Mutate(string oldValue, string newValue)
    {
        var text = ReadWorkflowText();
        text.ShouldContain(oldValue, customMessage: $"Known-bad sample precondition: '{oldValue}' not in {RelativeFilePath}.");
        return text.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private sealed record Step(string Name, string Body);

    private static string ReadWorkflowText()
    {
        var fullPath = Path.Combine(FindRepoRoot(), RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static List<Step> ReadSteps(string workflowText)
    {
        var lines = StripWholeLineComments(workflowText).Split('\n');
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
