using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.21 (AD-13, AD-2): a self-host install reaches the current schema because docker-compose.yml runs
// a one-shot `migrate` service (the `migrate` Dockerfile target, an EF migrations bundle) before `api`, and
// `api` waits for it to exit 0. Azure keeps migrating in CI, so nothing outside the compose file may build
// that target. Compose and workflow files are scanned as text with whole-line `#` comments stripped, and each
// rule is a function over the text so it is also proven against known-bad text (non-vacuous).
public class SelfHostMigrationIsOneShotServiceTests
{
    private const string ComposePath = "docker-compose.yml";
    private const string EntrypointPath = "scripts/self-host/migrate-entrypoint.sh";
    private const string SelfHostingDocPath = "docs/self-hosting.md";

    // The failure message of the migrate service. The script prints these lines and docs/self-hosting.md quotes
    // them, so an operator can search the guide for what the log says.
    private static readonly string[] PinnedMessageLines =
    [
        "migrate: FAILED. The migration did not finish, so the api container was not started.",
        "migrate: likely cause: database unreachable, POSTGRES_PASSWORD in .env does not match the existing postgres-data volume, or a migration failed.",
        "migrate: read the cause with: docker compose logs migrate",
        "migrate: after fixing it, run: docker compose up -d (details: docs/self-hosting.md, 'Database migrations')",
    ];

    private const string KnownGoodCompose = """
        services:
          migrate:
            build:
              context: .
              target: migrate
            depends_on:
              postgres:
                condition: service_healthy
            restart: "no"

          api:
            build: .
            depends_on:
              migrate:
                condition: service_completed_successfully
              postgres:
                condition: service_healthy

          postgres:
            image: postgres:18-alpine
        """;

    // The shape of scripts/self-host/migrate-entrypoint.sh without the failure message lines.
    private const string KnownGoodEntrypoint = """
        #!/bin/sh
        set -u
        /app/efbundle &
        pid=$!
        trap 'kill -TERM "$pid" 2>/dev/null' TERM INT
        wait "$pid"
        status=$?
        if kill -0 "$pid" 2>/dev/null; then
            wait "$pid"
            status=$?
        fi
        exit "$status"
        """;

    [Fact]
    public void The_real_compose_file_runs_migrate_once_before_api()
    {
        ComposeViolations(ReadRepoFile(ComposePath)).ShouldBeEmpty(
            "docker-compose.yml must run the one-shot `migrate` service before `api` (Story 11.21, AD-13).");
    }

    [Fact]
    public void The_rules_accept_a_known_good_compose_file()
    {
        ComposeViolations(KnownGoodCompose).ShouldBeEmpty();
    }

    [Fact]
    public void The_rules_reject_a_compose_file_without_a_migrate_service()
    {
        var mutated = KnownGoodCompose[..KnownGoodCompose.IndexOf("  migrate:", StringComparison.Ordinal)]
            + KnownGoodCompose[KnownGoodCompose.IndexOf("  api:", StringComparison.Ordinal)..];

        ComposeViolations(mutated).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("target: migrate", "target: runtime")]
    [InlineData("restart: \"no\"", "restart: always")]
    [InlineData("restart: \"no\"", "restart: unless-stopped")]
    public void The_rules_reject_a_migrate_service_that_is_not_a_one_shot_of_the_migrate_target(string find, string replace)
    {
        ComposeViolations(KnownGoodCompose.Replace(find, replace, StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_reject_a_migrate_service_that_does_not_wait_for_a_healthy_postgres()
    {
        var mutated = KnownGoodCompose.Replace(
            "postgres:\n        condition: service_healthy\n    restart",
            "postgres:\n        condition: service_started\n    restart",
            StringComparison.Ordinal);

        mutated.ShouldNotBe(KnownGoodCompose);
        ComposeViolations(mutated).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("condition: service_completed_successfully", "condition: service_started")]
    [InlineData("condition: service_completed_successfully", "condition: service_healthy")]
    public void The_rules_reject_an_api_that_does_not_wait_for_migrate_to_succeed(string find, string replace)
    {
        ComposeViolations(KnownGoodCompose.Replace(find, replace, StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_reject_an_api_without_a_dependency_on_migrate()
    {
        var mutated = KnownGoodCompose.Replace(
            "      migrate:\n        condition: service_completed_successfully\n", "", StringComparison.Ordinal);

        mutated.ShouldNotBe(KnownGoodCompose);
        ComposeViolations(mutated).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_ignore_a_commented_out_dependency()
    {
        var mutated = KnownGoodCompose.Replace(
            "      migrate:\n        condition: service_completed_successfully\n",
            "      # migrate:\n      #   condition: service_completed_successfully\n",
            StringComparison.Ordinal);

        ComposeViolations(mutated).ShouldNotBeEmpty();
    }

    [Fact]
    public void No_workflow_or_infra_file_builds_the_migrate_target()
    {
        var root = FindRepoRoot();
        var workflows = Path.Combine(root, ".github", "workflows");
        var infra = Path.Combine(root, "infra");
        var files = new List<string>();
        if (Directory.Exists(workflows))
        {
            files.AddRange(Directory.EnumerateFiles(workflows, "*.yml"));
            files.AddRange(Directory.EnumerateFiles(workflows, "*.yaml"));
        }

        if (Directory.Exists(infra))
        {
            files.AddRange(Directory.EnumerateFiles(infra, "*", SearchOption.AllDirectories));
        }

        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            BuildsTheMigrateTarget(File.ReadAllText(file)).ShouldBeFalse(
                $"{Path.GetRelativePath(root, file)} must not build the Dockerfile `migrate` target: Azure migrates in CI (Story 10.1) and the Container App never migrates itself.");
        }
    }

    [Theory]
    [InlineData("          target: migrate")]
    [InlineData("          target: \"migrate\"")]
    [InlineData("          target: migrate # build the bundle")]
    [InlineData("        run: docker build --target migrate .")]
    [InlineData("        run: docker buildx build --target=migrate .")]
    public void The_target_rule_catches_a_workflow_that_builds_migrate(string line)
    {
        BuildsTheMigrateTarget($"jobs:\n  image-build:\n    steps:\n{line}\n").ShouldBeTrue();
    }

    [Theory]
    [InlineData("          target: runtime")]
    [InlineData("        # target: migrate")]
    [InlineData("        run: docker build .")]
    public void The_target_rule_ignores_everything_else(string line)
    {
        BuildsTheMigrateTarget($"jobs:\n  image-build:\n    steps:\n{line}\n").ShouldBeFalse();
    }

    [Fact]
    public void The_entrypoint_script_exists_runs_the_bundle_and_keeps_its_exit_code()
    {
        EntrypointViolations(ReadRepoFile(EntrypointPath)).ShouldBeEmpty();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(Path.Combine(FindRepoRoot(), EntrypointPath)).HasFlag(UnixFileMode.UserExecute)
                .ShouldBeTrue("The script is the container ENTRYPOINT: mode 0755 in git.");
        }
    }

    [Fact]
    public void The_entrypoint_rules_accept_a_known_good_script()
    {
        EntrypointViolations(KnownGoodEntrypoint).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("exit \"$status\"", "exit 0")]
    [InlineData("exit \"$status\"", "")]
    [InlineData("set -u", "set -eu")]
    [InlineData("/app/efbundle &", "/app/efbundle")]
    [InlineData("trap 'kill -TERM \"$pid\" 2>/dev/null' TERM INT", "")]
    [InlineData("wait \"$pid\"\nstatus=$?", "wait \"$pid\"\ntrue")]
    [InlineData("#!/bin/sh", "#!/bin/bash")]
    public void The_entrypoint_rules_reject_a_script_that_loses_the_exit_code_or_the_signal_forwarding(string find, string replace)
    {
        var mutated = KnownGoodEntrypoint.Replace(find, replace, StringComparison.Ordinal);

        mutated.ShouldNotBe(KnownGoodEntrypoint);
        EntrypointViolations(mutated).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_entrypoint_rules_reject_a_script_with_windows_line_endings()
    {
        EntrypointViolations(KnownGoodEntrypoint.Replace("\n", "\r\n", StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_pinned_failure_message_is_printed_by_the_script_and_quoted_in_the_self_hosting_guide()
    {
        var script = ReadRepoFile(EntrypointPath);
        var doc = ReadRepoFile(SelfHostingDocPath);

        foreach (var line in PinnedMessageLines)
        {
            script.ShouldContain(line, Case.Sensitive, $"{EntrypointPath} must print: {line}");
            doc.ShouldContain(line, Case.Sensitive, $"{SelfHostingDocPath} must quote the log line: {line}");
        }
    }

    // ---- the rules ----

    // Empty when the compose text follows every rule; otherwise one sentence per broken rule.
    private static List<string> ComposeViolations(string composeText)
    {
        var violations = new List<string>();
        var services = Services(StripWholeLineComments(composeText));

        if (!services.TryGetValue("migrate", out var migrate))
        {
            violations.Add("docker-compose.yml has no `migrate` service.");
        }
        else
        {
            if (!Regex.IsMatch(migrate, @"^\s+target:\s*migrate\s*$", RegexOptions.Multiline))
            {
                violations.Add("Service `migrate` must build `target: migrate`.");
            }

            if (!Regex.IsMatch(migrate, @"^\s+restart:\s*""no""\s*$", RegexOptions.Multiline))
            {
                violations.Add("Service `migrate` must be one-shot: `restart: \"no\"`.");
            }

            if (!DependsOn(migrate, "postgres", "service_healthy"))
            {
                violations.Add("Service `migrate` must wait for `postgres` with `condition: service_healthy`.");
            }
        }

        if (!services.TryGetValue("api", out var api) || !DependsOn(api, "migrate", "service_completed_successfully"))
        {
            violations.Add("Service `api` must wait for `migrate` with `condition: service_completed_successfully`.");
        }

        return violations;
    }

    // Empty when the script follows every rule; otherwise one sentence per broken rule.
    private static List<string> EntrypointViolations(string script)
    {
        var violations = new List<string>();
        var lines = script.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();

        if (!script.StartsWith("#!/bin/sh", StringComparison.Ordinal))
        {
            violations.Add("POSIX sh shebang `#!/bin/sh` first: the migrate image has no bash.");
        }

        if (script.Contains('\r'))
        {
            violations.Add("LF line endings only: a CRLF shebang makes the container fail with 'no such file or directory'.");
        }

        if (Regex.IsMatch(script, @"^\s*set\s+-\w*e", RegexOptions.Multiline))
        {
            violations.Add("No `set -e`: the script must reach the failure message and exit with the bundle's code.");
        }

        if (!Regex.IsMatch(script, @"^/app/efbundle\s*&\s*$", RegexOptions.Multiline))
        {
            violations.Add("Run `/app/efbundle &` in the background so the shell can forward signals to it.");
        }

        if (!Regex.IsMatch(script, @"^trap\s+'[^']*kill[^']*'\s+TERM\b", RegexOptions.Multiline))
        {
            violations.Add("Forward SIGTERM to the bundle with `trap '... kill ...' TERM INT`, or `docker compose stop` can kill a migration mid-run.");
        }

        if (!Regex.IsMatch(script, @"^wait\s+""\$pid""\s*\nstatus=\$\?", RegexOptions.Multiline))
        {
            violations.Add("`wait \"$pid\"` must be followed directly by `status=$?` to capture the bundle's exit code.");
        }

        if (lines.Count == 0 || !Regex.IsMatch(lines[^1], @"^exit\s+""\$status""$"))
        {
            violations.Add("The script must end with `exit \"$status\"`, or `service_completed_successfully` would not see a failure.");
        }

        return violations;
    }

    private static bool DependsOn(string serviceBlock, string dependency, string condition) =>
        Regex.IsMatch(serviceBlock, $@"^\s+{dependency}:\s*\n\s+condition:\s*{condition}\s*$", RegexOptions.Multiline);

    // Service name to its block of text; services sit at two-space indent directly under `services:`.
    private static Dictionary<string, string> Services(string text)
    {
        var result = new Dictionary<string, string>();
        var inServices = false;
        string? current = null;
        var block = new List<string>();

        void Flush()
        {
            if (current is not null)
            {
                result[current] = string.Join('\n', block);
            }

            block.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\S"))
            {
                Flush();
                current = null;
                inServices = line.TrimEnd() == "services:";
                continue;
            }

            var start = Regex.Match(line, @"^  (?<name>[A-Za-z0-9_-]+):\s*$");
            if (inServices && start.Success)
            {
                Flush();
                current = start.Groups["name"].Value;
                continue;
            }

            if (current is not null)
            {
                block.Add(line);
            }
        }

        Flush();
        return result;
    }

    private static bool BuildsTheMigrateTarget(string text) =>
        Regex.IsMatch(StripWholeLineComments(text), @"(^\s*target:\s*[""']?migrate[""']?\s*(#.*)?$)|(--target[ =]migrate\b)", RegexOptions.Multiline);

    private static string StripWholeLineComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string ReadRepoFile(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        return File.ReadAllText(fullPath);
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
