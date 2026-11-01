using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.11 (audit S5): the shipped image is built the way CI verified the code, and it does not run as
// root. Both are properties of a text file that the next edit can silently undo, so they are executable:
// the backend-build stage must copy global.json (the SDK pin) and Directory.Build.props (the NuGet-audit
// gate) before its first `dotnet restore`, and the runtime stage must switch to a non-root user before
// ENTRYPOINT and never switch back. A source-text scan with comments stripped, the same convention as
// HouseholdImportIsStreamedTests; the rules are a function over the Dockerfile text so the test can also
// prove them against known-bad text (non-vacuous), not only against the real file.
public class DockerImageMatchesTheVerifiedBuildTests
{
    private const string DockerfilePath = "Dockerfile";

    private const string KnownGood = """
        FROM node:26-alpine AS frontend-build
        RUN npm run build
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
        COPY global.json Directory.Build.props Directory.Packages.props ./
        COPY src/ ./src/
        RUN dotnet restore src/Api/Api.csproj
        FROM backend-build AS migrate-build
        COPY .config ./.config
        RUN dotnet tool restore
        RUN RID=linux-x64 && dotnet ef migrations bundle --self-contained -r "$RID" -o /app/efbundle
        FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrate
        COPY --from=migrate-build /app/efbundle /app/efbundle
        COPY scripts/self-host/migrate-entrypoint.sh /app/migrate-entrypoint.sh
        USER $APP_UID
        ENTRYPOINT ["/app/migrate-entrypoint.sh"]
        FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
        COPY --from=backend-build /app/publish .
        USER $APP_UID
        ENTRYPOINT ["dotnet", "Api.dll"]
        """;

    [Fact]
    public void The_real_Dockerfile_builds_what_CI_verified_and_runs_as_a_non_root_user()
    {
        var dockerfile = ReadDockerfile();

        Violations(dockerfile).ShouldBeEmpty(
            "The Dockerfile must copy global.json and Directory.Build.props before `dotnet restore` and run the runtime stage as a non-root user (Story 11.11, audit S5).");
    }

    [Fact]
    public void The_real_Dockerfile_still_has_the_shape_the_rules_assume()
    {
        var instructions = Instructions(StripComments(ReadDockerfile()));

        instructions.Count(i => i.Keyword == "FROM").ShouldBe(5, "Expected the five stages frontend-build, backend-build, migrate-build, migrate and runtime.");
        var lastFrom = instructions.Last(i => i.Keyword == "FROM");
        lastFrom.Arguments.ShouldContain("mcr.microsoft.com/dotnet/aspnet", Case.Insensitive, "The last stage must be the aspnet runtime stage.");
        instructions.ShouldContain(i => i.Keyword == "ENTRYPOINT", "The runtime stage must have an ENTRYPOINT.");
        instructions.ShouldContain(
            i => i.Keyword == "RUN" && i.Arguments.StartsWith("dotnet restore", StringComparison.Ordinal),
            "The backend-build stage must still run `dotnet restore` (the rule about what is copied before it needs one).");
    }

    [Fact]
    public void The_rules_accept_a_known_good_Dockerfile()
    {
        Violations(KnownGood).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("global.json Directory.Build.props", "Directory.Build.props")]
    [InlineData("global.json Directory.Build.props", "global.json")]
    public void The_rules_reject_a_build_stage_that_does_not_copy_both_root_files(string find, string replace)
    {
        var mutated = KnownGood.Replace(find, replace, StringComparison.Ordinal);

        Violations(mutated).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_reject_root_files_copied_only_after_the_restore()
    {
        var mutated = KnownGood
            .Replace("COPY global.json Directory.Build.props Directory.Packages.props ./\n", "", StringComparison.Ordinal)
            .Replace(
                "RUN dotnet restore src/Api/Api.csproj\n",
                "RUN dotnet restore src/Api/Api.csproj\nCOPY global.json Directory.Build.props ./\n",
                StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("before", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_runtime_stage_without_a_USER()
    {
        Violations(KnownGood.Replace("USER $APP_UID\n", "", StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_reject_a_USER_that_comes_after_the_ENTRYPOINT()
    {
        var mutated = KnownGood
            .Replace("USER $APP_UID\n", "", StringComparison.Ordinal)
            .Replace("ENTRYPOINT [\"dotnet\", \"Api.dll\"]", "ENTRYPOINT [\"dotnet\", \"Api.dll\"]\nUSER $APP_UID", StringComparison.Ordinal);

        Violations(mutated).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("USER root")]
    [InlineData("USER 0")]
    [InlineData("USER 0:0")]
    public void The_rules_reject_a_USER_that_is_root(string user)
    {
        Violations(KnownGood.Replace("USER $APP_UID", user, StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("USER root")]
    [InlineData("USER 0")]
    [InlineData("USER 0:")]
    public void The_rules_reject_a_switch_back_to_root_after_the_non_root_USER(string later)
    {
        var mutated = KnownGood.Replace("USER $APP_UID\n", $"USER $APP_UID\n{later}\n", StringComparison.Ordinal);

        Violations(mutated).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("USER $APP_UID")]
    [InlineData("USER ${APP_UID}")]
    [InlineData("USER 1654")]
    [InlineData("USER 1654:1654")]
    public void The_rules_accept_the_non_root_forms(string user)
    {
        Violations(KnownGood.Replace("USER $APP_UID", user, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ENV APP_UID=0")]
    [InlineData("ENV APP_UID 0")]
    [InlineData("ENV A=1 APP_UID=0")]
    [InlineData("ENV APP_UID=\"root\"")]
    [InlineData("ARG APP_UID=0")]
    [InlineData("ARG APP_UID=")]
    public void The_rules_reject_a_runtime_stage_that_redefines_APP_UID_to_root(string redefinition)
    {
        var mutated = KnownGood.Replace("USER $APP_UID", $"{redefinition}\nUSER $APP_UID", StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("APP_UID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ENV APP_UID=1654")]
    [InlineData("ARG APP_UID")]
    [InlineData("ENV OTHER=$APP_UID")]
    [InlineData("# ENV APP_UID=0")]
    public void The_rules_accept_a_runtime_stage_that_keeps_APP_UID_non_root(string line)
    {
        var mutated = KnownGood.Replace("USER $APP_UID", $"{line}\nUSER $APP_UID", StringComparison.Ordinal);

        Violations(mutated).ShouldBeEmpty();
    }

    [Fact]
    public void The_rules_ignore_comments_and_flag_a_missing_stage()
    {
        // A commented-out USER does not count; neither does a commented-out COPY.
        var mutated = KnownGood
            .Replace("USER $APP_UID", "# USER $APP_UID", StringComparison.Ordinal)
            .Replace("COPY global.json Directory.Build.props Directory.Packages.props ./", "# COPY global.json Directory.Build.props ./", StringComparison.Ordinal);
        Violations(mutated).Count.ShouldBeGreaterThanOrEqualTo(2);

        Violations("FROM a AS one\nFROM b AS two\nENTRYPOINT [\"x\"]").ShouldNotBeEmpty("Two stages is not the expected shape.");
    }

    [Fact]
    public void The_rules_reject_a_migrate_stage_that_ends_the_file()
    {
        var runtimeStart = KnownGood.IndexOf("FROM mcr.microsoft.com/dotnet/aspnet", StringComparison.Ordinal);
        var migrateStart = KnownGood.IndexOf("FROM mcr.microsoft.com/dotnet/runtime-deps", StringComparison.Ordinal);
        var migrate = KnownGood[migrateStart..runtimeStart];
        var mutated = KnownGood[..migrateStart] + KnownGood[runtimeStart..] + "\n" + migrate;

        Violations(mutated).ShouldContain(v => v.Contains("last stage", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("USER root")]
    [InlineData("USER 0")]
    public void The_rules_reject_a_migrate_stage_that_runs_as_root(string user)
    {
        var mutated = KnownGood.Replace(
            "COPY scripts/self-host/migrate-entrypoint.sh /app/migrate-entrypoint.sh\nUSER $APP_UID",
            $"COPY scripts/self-host/migrate-entrypoint.sh /app/migrate-entrypoint.sh\n{user}",
            StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("migrate stage", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_migrate_stage_without_an_ENTRYPOINT()
    {
        var mutated = KnownGood.Replace("ENTRYPOINT [\"/app/migrate-entrypoint.sh\"]\n", "", StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("migrate stage has no ENTRYPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_migrate_stage_that_does_not_copy_the_entrypoint_script()
    {
        var mutated = KnownGood.Replace(
            "COPY scripts/self-host/migrate-entrypoint.sh /app/migrate-entrypoint.sh\n", "", StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("migrate-entrypoint.sh", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_migrate_build_that_does_not_start_from_the_verified_build_stage()
    {
        var mutated = KnownGood.Replace("FROM backend-build AS migrate-build", "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS migrate-build", StringComparison.Ordinal);

        Violations(mutated).ShouldContain(v => v.Contains("migrate-build", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_migrate_bundle_that_is_not_self_contained()
    {
        var mutated = KnownGood.Replace("--self-contained ", "", StringComparison.Ordinal);

        mutated.ShouldNotBe(KnownGood);
        Violations(mutated).ShouldContain(v => v.Contains("self-contained", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_reject_a_duplicate_stage_name()
    {
        var mutated = KnownGood.Replace("AS migrate-build", "AS migrate", StringComparison.Ordinal);

        mutated.ShouldNotBe(KnownGood);
        Violations(mutated).ShouldNotBeEmpty();
    }

    // ---- the rules ----

    // Empty when the Dockerfile text follows every rule; otherwise one sentence per broken rule.
    private static List<string> Violations(string dockerfileText)
    {
        var violations = new List<string>();
        var instructions = Instructions(StripComments(dockerfileText));

        var stageList = Stages(instructions);
        var names = stageList.Select(s => s.Name).ToList();
        string[] required = ["frontend-build", "backend-build", "migrate-build", "migrate", "runtime"];
        if (names.Count != required.Length || names.Distinct().Count() != names.Count || required.Any(name => !names.Contains(name)))
        {
            violations.Add($"Expected exactly the stages {string.Join(", ", required)}, each once, found {string.Join(", ", names)}.");
            return violations;
        }

        var stages = stageList.ToDictionary(s => s.Name, s => s.Instructions);
        if (names[^1] != "runtime")
        {
            violations.Add("The runtime stage must be the last stage: `docker build .` (CI, Azure) builds the last stage, and it must stay the API image.");
        }

        CheckBuildStage(stages["backend-build"], violations);
        CheckMigrateBuildStage(stages["migrate-build"], violations);
        CheckMigrateStage(stages["migrate"], violations);
        CheckNonRootStage(stages["runtime"], "runtime", violations);
        return violations;
    }

    // Stages by their `AS name`, in file order; each stage list starts with its FROM.
    private static List<(string Name, List<Instruction> Instructions)> Stages(List<Instruction> instructions)
    {
        var stages = new List<(string Name, List<Instruction> Instructions)>();
        List<Instruction>? current = null;
        foreach (var instruction in instructions)
        {
            if (instruction.Keyword == "FROM")
            {
                var name = Regex.Match(instruction.Arguments, @"\sAS\s+(\S+)\s*$", RegexOptions.IgnoreCase);
                current = [];
                stages.Add((name.Success ? name.Groups[1].Value : $"unnamed-{stages.Count}", current));
            }

            current?.Add(instruction);
        }

        return stages;
    }

    private static void CheckMigrateBuildStage(List<Instruction> stage, List<string> violations)
    {
        if (!stage[0].Arguments.StartsWith("backend-build ", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add("The migrate-build stage must start `FROM backend-build`, so the bundle is built from the same pinned SDK and NuGet-audit inputs as the API.");
        }

        if (!stage.Any(i => i.Keyword == "RUN" && i.Arguments.Contains("dotnet ef migrations bundle", StringComparison.Ordinal)))
        {
            violations.Add("The migrate-build stage has no `dotnet ef migrations bundle`.");
        }

        if (!stage.Any(i => i.Keyword == "RUN" && i.Arguments.Contains("--self-contained", StringComparison.Ordinal)
            && Regex.IsMatch(i.Arguments, @"\s-r\s+""?\$\{?RID\}?""?")))
        {
            violations.Add("The migrate-build bundle must be `--self-contained -r \"$RID\"`: the migrate stage runs on runtime-deps, which has no .NET runtime.");
        }
    }

    private static void CheckMigrateStage(List<Instruction> stage, List<string> violations)
    {
        CheckNonRootStage(stage, "migrate", violations);

        if (!stage.Any(i => i.Keyword == "COPY" && i.Arguments.Contains("migrate-entrypoint.sh", StringComparison.Ordinal)))
        {
            violations.Add("The migrate stage must COPY scripts/self-host/migrate-entrypoint.sh: it prints the failure message and keeps the bundle's exit code.");
        }
    }

    private static void CheckBuildStage(List<Instruction> stage, List<string> violations)
    {
        var restoreIndex = stage.FindIndex(i => i.Keyword == "RUN" && i.Arguments.Contains("dotnet restore", StringComparison.Ordinal));
        if (restoreIndex < 0)
        {
            violations.Add("The backend-build stage has no `dotnet restore`.");
            return;
        }

        var copiedBeforeRestore = string.Join(' ', stage[..restoreIndex].Where(i => i.Keyword == "COPY").Select(i => i.Arguments));
        foreach (var required in new[] { "global.json", "Directory.Build.props" })
        {
            if (!copiedBeforeRestore.Contains(required, StringComparison.Ordinal))
            {
                violations.Add($"The backend-build stage must COPY {required} before its first `dotnet restore`, or the shipped image is not built the way CI verified it.");
            }
        }
    }

    private static void CheckNonRootStage(List<Instruction> stage, string name, List<string> violations)
    {
        var entrypointIndex = stage.FindIndex(i => i.Keyword == "ENTRYPOINT");
        if (entrypointIndex < 0)
        {
            violations.Add($"The {name} stage has no ENTRYPOINT.");
            return;
        }

        var firstSafeUserIndex = stage.FindIndex(i => i.Keyword == "USER" && IsNonRoot(i.Arguments));
        if (firstSafeUserIndex < 0 || firstSafeUserIndex > entrypointIndex)
        {
            violations.Add($"The {name} stage needs `USER $APP_UID` (or a non-zero numeric id) before ENTRYPOINT.");
            return;
        }

        foreach (var later in stage[(firstSafeUserIndex + 1)..].Where(i => i.Keyword == "USER" && !IsNonRoot(i.Arguments)))
        {
            violations.Add($"`USER {later.Arguments}` appears after the non-root USER in the {name} stage: the container would run as root again.");
        }

        // `USER $APP_UID` is only as non-root as the variable: the stage must not redefine it to root (or to
        // anything but a non-zero number).
        foreach (var value in stage.Where(i => i.Keyword is "ENV" or "ARG").SelectMany(AppUidAssignments)
                     .Where(value => !Regex.IsMatch(value, @"^[1-9][0-9]*$")))
        {
            violations.Add($"The {name} stage sets APP_UID to `{value}`: `USER $APP_UID` would not be a non-root user.");
        }
    }

    // The values an ENV/ARG instruction assigns to APP_UID: `APP_UID=0`, `A=1 APP_UID=0` and the legacy
    // `ENV APP_UID 0` form. A bare `ARG APP_UID` assigns nothing.
    private static IEnumerable<string> AppUidAssignments(Instruction instruction)
    {
        foreach (Match match in Regex.Matches(instruction.Arguments, @"(?<![\w$])APP_UID\s*=\s*(\S*)"))
        {
            yield return match.Groups[1].Value.Trim('"', '\'');
        }

        if (instruction.Keyword == "ENV")
        {
            var legacy = Regex.Match(instruction.Arguments, @"^APP_UID\s+([^=\s]\S*)");
            if (legacy.Success)
            {
                yield return legacy.Groups[1].Value.Trim('"', '\'');
            }
        }
    }

    // $APP_UID is the aspnet image's own non-root id (1654). A name is not enough for a runAsNonRoot check,
    // so only the variable or a non-zero numeric id (optionally :gid) is accepted; root and 0 never are.
    private static bool IsNonRoot(string user) =>
        user is "$APP_UID" or "${APP_UID}" || Regex.IsMatch(user, @"^[1-9][0-9]*(:[0-9]+)?$");

    private sealed record Instruction(string Keyword, string Arguments);

    // One instruction per logical line (a trailing backslash continues the line), keyword upper-cased.
    private static List<Instruction> Instructions(string strippedText)
    {
        var result = new List<Instruction>();
        var pending = "";
        foreach (var rawLine in strippedText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                pending += line[..^1].TrimEnd() + " ";
                continue;
            }

            line = (pending + line).Trim();
            pending = "";
            var split = line.IndexOfAny([' ', '\t']);
            var keyword = (split < 0 ? line : line[..split]).ToUpperInvariant();
            var arguments = split < 0 ? "" : line[(split + 1)..].Trim();
            result.Add(new Instruction(keyword, arguments));
        }

        return result;
    }

    // A Dockerfile comment is a line whose first non-blank character is '#'.
    private static string StripComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string ReadDockerfile()
    {
        var fullPath = Path.Combine(FindRepoRoot(), DockerfilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        var text = File.ReadAllText(fullPath);
        StripComments(text).Trim().ShouldNotBeEmpty($"{DockerfilePath} has no instructions after stripping comments.");
        return text;
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
