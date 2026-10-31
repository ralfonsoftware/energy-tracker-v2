using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.18 (audit R3): the backend has an analyzer and format gate. Directory.Build.props switches the
// SDK analyzers on (AnalysisMode Recommended, security rules All), pr-review.yml builds with
// TreatWarningsAsErrors and verifies `dotnet format`, and the only ways to lower a rule are a commented
// .editorconfig line or a justified SuppressMessage. All of that is text a later edit can quietly undo, so
// it is executable. Same convention as DockerImageMatchesTheVerifiedBuildTests: each rule is a function
// over file text, proven against known-bad text (non-vacuous) as well as against the real files.
public partial class BackendAnalyzerGateIsConfiguredTests
{
    private const string PropsPath = "Directory.Build.props";
    private const string EditorConfigPath = ".editorconfig";
    private const string WorkflowPath = ".github/workflows/pr-review.yml";

    // The known-bad samples below are assembled from pieces on purpose: this file is itself under tests/ and
    // is scanned, and a contiguous directive or attribute inside a string literal would be reported.
    private const string Hash = "#";
    private const string Suppress = "Suppress" + "Message";
    private const string NoWarnTag = "No" + "Warn";
    private static readonly string[] CodeRoots = ["src", "tests"];
    private static readonly string[] BuildFilePatterns = ["*.csproj", "*.props", "*.targets"];
    private static readonly string[] RootAnalyzerConfigs = [".globalconfig"];
    private static readonly string[] RootBuildFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    [Fact]
    public void Directory_Build_props_turns_the_analyzers_on()
    {
        PropsViolations(ReadRepoFile(PropsPath)).ShouldBeEmpty(
            "Directory.Build.props must set AnalysisMode=Recommended and AnalysisModeSecurity=All (Story 11.18, audit R3).");
    }

    [Fact]
    public void The_editorconfig_is_the_repo_root_and_every_lowered_rule_says_why()
    {
        EditorConfigViolations(ReadRepoFile(EditorConfigPath)).ShouldBeEmpty(
            "Root .editorconfig must start with root = true, and a line that lowers a rule needs a # comment on the line directly above it (Story 11.18).");
    }

    [Fact]
    public void The_PR_workflow_builds_with_warnings_as_errors_and_verifies_formatting()
    {
        WorkflowViolations(ReadRepoFile(WorkflowPath)).ShouldBeEmpty(
            "pr-review.yml must build with -p:TreatWarningsAsErrors=true and run `dotnet format ... --verify-no-changes` (Story 11.18).");
    }

    [Fact]
    public void No_source_file_silences_a_warning_without_a_reason()
    {
        var root = FindRepoRoot();
        var files = CSharpFiles(root);

        var violations = files
            .SelectMany(f => SuppressionViolations(CSharpSource.StripComments(File.ReadAllText(Path.Combine(root, f)))).Select(v => $"{f} ({v})"))
            .ToList();

        violations.ShouldBeEmpty(
            "Suppress a rule with a commented .editorconfig line or a SuppressMessage attribute with a Justification on the member, never with a pragma (Story 11.18). "
            + string.Join("; ", violations));
    }

    [Fact]
    public void No_build_file_weakens_the_analyzers()
    {
        var root = FindRepoRoot();
        // The solution's projects only: spikes/ holds throwaway projects that are not in EnergyTracker.sln.
        var files = CodeRoots
            .SelectMany(top => BuildFilePatterns
                .SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(root, top), pattern, SearchOption.AllDirectories)))
            .Concat(RootBuildFiles.Select(f => Path.Combine(root, f)).Where(File.Exists))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildPath(f))
            .Distinct()
            .ToList();

        files.Count.ShouldBeGreaterThan(5, "the scan found almost no project files: did the layout move? Update this guard with it.");

        var violations = files
            .SelectMany(f => ProjectViolations(File.ReadAllText(Path.Combine(root, f)), f == PropsPath).Select(v => $"{f} ({v})"))
            .ToList();

        violations.ShouldBeEmpty(
            "a build file may not hide diagnostics or switch analyzers off: lower the rule in .editorconfig with a comment instead (Story 11.18). " + string.Join("; ", violations));
    }

    [Fact]
    public void No_other_analyzer_config_file_exists()
    {
        var root = FindRepoRoot();
        var files = CodeRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildPath(f))
            .Where(f => Path.GetFileName(f) is ".editorconfig" or ".globalconfig" || f.EndsWith(".globalconfig", StringComparison.Ordinal))
            .Concat(RootAnalyzerConfigs.Where(f => File.Exists(Path.Combine(root, f))))
            .ToList();

        files.ShouldBeEmpty("the root .editorconfig is the only analyzer configuration, so the guard above sees every lowered rule (Story 11.18). " + string.Join("; ", files));
    }

    [Fact]
    public void The_scan_covers_src_and_tests()
    {
        var files = CSharpFiles(FindRepoRoot());

        files.Count(f => f.StartsWith("src/", StringComparison.Ordinal)).ShouldBeGreaterThan(100, "the scan found almost no src files: did the layout move? Update this guard with it.");
        files.Count(f => f.StartsWith("tests/", StringComparison.Ordinal)).ShouldBeGreaterThan(50, "the scan found almost no test files: did the layout move? Update this guard with it.");
        files.ShouldNotContain(f => f.Contains("/obj/", StringComparison.Ordinal) || f.Contains("/bin/", StringComparison.Ordinal));
    }

    // ---- Non-vacuous: the rules are proven against known-bad text, so a refactor cannot turn them into no-ops.

    [Fact]
    public void The_props_rule_reports_a_missing_or_weaker_setting()
    {
        const string good = """
            <Project>
              <PropertyGroup>
                <AnalysisMode>Recommended</AnalysisMode>
                <AnalysisModeSecurity>All</AnalysisModeSecurity>
              </PropertyGroup>
            </Project>
            """;
        PropsViolations(good).ShouldBeEmpty();

        PropsViolations("<Project><PropertyGroup><NuGetAuditMode>all</NuGetAuditMode></PropertyGroup></Project>").Count.ShouldBe(2);
        PropsViolations(good.Replace("Recommended", "Minimum", StringComparison.Ordinal)).ShouldBe(["AnalysisMode is not Recommended"]);
        PropsViolations(good.Replace(">All<", ">Default<", StringComparison.Ordinal)).ShouldBe(["AnalysisModeSecurity is not All"]);

        // A setting that only exists inside an XML comment does not count.
        PropsViolations("<Project><!-- <AnalysisMode>Recommended</AnalysisMode><AnalysisModeSecurity>All</AnalysisModeSecurity> --></Project>").Count.ShouldBe(2);
    }

    [Fact]
    public void The_editorconfig_rule_reports_a_missing_root_and_an_unexplained_lowered_rule()
    {
        const string good = """
            # Editor and analyzer settings.
            root = true

            [*.cs]
            # Why this rule is off.
            dotnet_diagnostic.CA1848.severity = none
            """;
        EditorConfigViolations(good).ShouldBeEmpty();

        EditorConfigViolations(good.Replace("root = true", "root = false", StringComparison.Ordinal)).ShouldContain(v => v.Contains("root = true", StringComparison.Ordinal));
        EditorConfigViolations("[*.cs]\nindent_size = 4").ShouldContain(v => v.Contains("root = true", StringComparison.Ordinal));

        EditorConfigViolations("root = true\n[*.cs]\ndotnet_diagnostic.CA1848.severity = none").Count.ShouldBe(1);
        EditorConfigViolations("root = true\n[*.cs]\ndotnet_diagnostic.CA1848.severity = silent").Count.ShouldBe(1);
        EditorConfigViolations("root = true\n[*.cs]\ndotnet_diagnostic.CA1848.severity = suggestion").Count.ShouldBe(1);

        // A blank line or another setting between the comment and the rule is not an explanation.
        EditorConfigViolations("root = true\n[*.cs]\n# why\n\ndotnet_diagnostic.CA1848.severity = none").Count.ShouldBe(1);
        EditorConfigViolations("root = true\n[*.cs]\n# why\nindent_size = 4\ndotnet_diagnostic.CA1848.severity = none").Count.ShouldBe(1);

        EditorConfigViolations("root = true\n[*.cs]\ndotnet_diagnostic.CA1848.severity = default").Count.ShouldBe(1);

        // A trailing comment on the root line still counts as root = true.
        EditorConfigViolations(good.Replace("root = true", "root = true # repo root", StringComparison.Ordinal)).ShouldBeEmpty();

        // Raising a rule needs no comment, and a commented-out lowering is not a lowering.
        EditorConfigViolations("root = true\n[*.cs]\ndotnet_diagnostic.CA1848.severity = warning").ShouldBeEmpty();
        EditorConfigViolations("root = true\n[*.cs]\n# dotnet_diagnostic.CA1848.severity = none").ShouldBeEmpty();
    }

    [Fact]
    public void The_workflow_rule_reports_a_missing_format_step_or_missing_warnings_as_errors()
    {
        const string good = """
            steps:
              - name: Build .NET solution
                run: dotnet build EnergyTracker.sln --no-restore --configuration Release -p:TreatWarningsAsErrors=true
              - name: Verify .NET formatting
                run: dotnet format EnergyTracker.sln --verify-no-changes --no-restore
            """;
        WorkflowViolations(good).ShouldBeEmpty();

        WorkflowViolations(good.Replace(" -p:TreatWarningsAsErrors=true", "", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations(good.Replace(" --verify-no-changes", "", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations("steps: []").Count.ShouldBe(2);

        // A step that only exists as a YAML comment does not count.
        WorkflowViolations(good.Replace("run: dotnet format", "# run: dotnet format", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations(good.Replace("run: dotnet build", "# run: dotnet build", StringComparison.Ordinal)).Count.ShouldBe(1);

        // A step that cannot fail the job, or never runs, does not gate anything.
        WorkflowViolations(good.Replace("--verify-no-changes --no-restore", "--verify-no-changes --no-restore || true", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations(good.Replace("    run: dotnet build", "    continue-on-error: true\n    run: dotnet build", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations(good.Replace("    run: dotnet format", "    if: false\n    run: dotnet format", StringComparison.Ordinal)).Count.ShouldBe(1);

        // A trailing comment is not part of the command.
        WorkflowViolations(good.Replace("EnergyTracker.sln --verify-no-changes", "EnergyTracker.sln # --verify-no-changes", StringComparison.Ordinal)).Count.ShouldBe(1);
        WorkflowViolations(good.Replace("Release -p:TreatWarningsAsErrors=true", "Release # -p:TreatWarningsAsErrors=true", StringComparison.Ordinal)).Count.ShouldBe(1);

        // The flag must be on the build, not just anywhere in the file.
        WorkflowViolations(good.Replace(" -p:TreatWarningsAsErrors=true", "", StringComparison.Ordinal) + "\n  run: dotnet test -p:TreatWarningsAsErrors=true").Count.ShouldBe(1);
    }

    [Fact]
    public void The_suppression_rule_reports_pragmas_and_unjustified_SuppressMessage_and_ignores_comments()
    {
        var pragma = $"{Hash}pragma warning disable CA1848\nvar x = 1;\n";
        SuppressionViolations(CSharpSource.StripComments(pragma)).Count.ShouldBe(1);
        SuppressionViolations(CSharpSource.StripComments($"  {Hash} pragma warning disable CA1848\n")).Count.ShouldBe(1);
        SuppressionViolations(CSharpSource.StripComments($"{Hash}pragma warning restore CA1848\n")).ShouldBeEmpty();

        var bare = $"[{Suppress}(\"Design\", \"CA1001:Types that own disposable fields\")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(bare)).Count.ShouldBe(1);
        var qualified = $"[System.Diagnostics.CodeAnalysis.{Suppress}(\"Design\", \"CA1001\")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(qualified)).Count.ShouldBe(1);
        var empty = $"[{Suppress}(\"Design\", \"CA1001\", Justification = \"\")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(empty)).Count.ShouldBe(1);
        var justified = $"[{Suppress}(\"Design\", \"CA1001\", Justification = \"The semaphore holds no unmanaged resource.\")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(justified)).ShouldBeEmpty();

        // Whitespace is not a reason.
        var blank = $"[{Suppress}(\"Design\", \"CA1001\", Justification = \"  \")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(blank)).Count.ShouldBe(1);

        // Combined attribute lists, module/assembly targets and a ] inside the Justification.
        var combined = $"[Fact, {Suppress}(\"Design\", \"CA1001\")]\nvoid M() {{ }}";
        SuppressionViolations(CSharpSource.StripComments(combined)).Count.ShouldBe(1);
        var assemblyTarget = $"[assembly: {Suppress}(\"Design\", \"CA1001\")]";
        SuppressionViolations(CSharpSource.StripComments(assemblyTarget)).Count.ShouldBe(1);
        var moduleTarget = $"[module: {Suppress}(\"Design\", \"CA1001\")]";
        SuppressionViolations(CSharpSource.StripComments(moduleTarget)).Count.ShouldBe(1);
        var bracket = $"[{Suppress}(\"Design\", \"CA1001\", Justification = \"Indexer [i] is read only.\")]\nclass A;";
        SuppressionViolations(CSharpSource.StripComments(bracket)).ShouldBeEmpty();
        var bracketUnjustified = $"[Fact, {Suppress}(\"Design\", \"CA1001:[x]\")]\nvoid M() {{ }}";
        SuppressionViolations(CSharpSource.StripComments(bracketUnjustified)).Count.ShouldBe(1);

        // Inside a comment it is not code.
        SuppressionViolations(CSharpSource.StripComments($"// {Hash}pragma warning disable CA1848\n")).ShouldBeEmpty();
        SuppressionViolations(CSharpSource.StripComments($"/* [{Suppress}(\"Design\", \"CA1001\")] */ class A;")).ShouldBeEmpty();
        SuppressionViolations(CSharpSource.StripComments($"// [{Suppress}(\"Design\", \"CA1001\")]\nclass A;")).ShouldBeEmpty();
    }

    [Fact]
    public void The_project_rule_reports_NoWarn_and_analyzer_switches_and_ignores_comments()
    {
        ProjectViolations($"<Project><PropertyGroup><{NoWarnTag}>CA1848</{NoWarnTag}></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations($"<Project><ItemGroup><{NoWarnTag} Include=\"CA1848\" /></ItemGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations($"<Project><!-- <{NoWarnTag}>CA1848</{NoWarnTag}> --><PropertyGroup /></Project>", false).ShouldBeEmpty();
        ProjectViolations("<Project><PropertyGroup><WarningsNotAsErrors>NU1900</WarningsNotAsErrors></PropertyGroup></Project>", true).ShouldBeEmpty();

        // Switching the analyzers off, or changing their mode, in any project or other build file.
        ProjectViolations("<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><AnalysisMode>None</AnalysisMode></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><AnalysisLevel>latest-minimum</AnalysisLevel></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>", false).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><WarningsNotAsErrors>CA1848</WarningsNotAsErrors></PropertyGroup></Project>", false).Count.ShouldBe(1);

        // The root props file owns AnalysisMode, but may not turn analyzers off or add warning exemptions.
        ProjectViolations("<Project><PropertyGroup><AnalysisMode>Recommended</AnalysisMode></PropertyGroup></Project>", true).ShouldBeEmpty();
        ProjectViolations("<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Project>", true).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><WarningsNotAsErrors>$(WarningsNotAsErrors);CA1848</WarningsNotAsErrors></PropertyGroup></Project>", true).Count.ShouldBe(1);
        ProjectViolations("<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>", true).ShouldBeEmpty();
    }

    [Fact]
    public void The_generated_migration_filter_matches_only_the_migration_folders()
    {
        IsGeneratedMigration("src/EnergyTracker.Infrastructure.Migrations.Postgres/Migrations/20260101_Init.cs").ShouldBeTrue();
        IsGeneratedMigration("src/EnergyTracker.Infrastructure.Migrations.SqlServer/Migrations/AppDbContextModelSnapshot.cs").ShouldBeTrue();
        IsGeneratedMigration("src/EnergyTracker.Api/Migrations/Hand.cs").ShouldBeFalse();
        IsGeneratedMigration("src/EnergyTracker.Infrastructure.Migrations.Postgres/Other.cs").ShouldBeFalse();
        IsGeneratedMigration("tests/EnergyTracker.Infrastructure.Tests/Migrations/Hand.cs").ShouldBeFalse();
    }

    // ---- The rules

    private static List<string> PropsViolations(string xml)
    {
        var properties = XDocument.Parse(xml).Descendants().Where(e => !e.HasElements).ToList();
        string? ValueOf(string name) => properties.LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        var violations = new List<string>();
        if (ValueOf("AnalysisMode") != "Recommended")
        {
            violations.Add("AnalysisMode is not Recommended");
        }

        if (ValueOf("AnalysisModeSecurity") != "All")
        {
            violations.Add("AnalysisModeSecurity is not All");
        }

        return violations;
    }

    private static List<string> EditorConfigViolations(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var violations = new List<string>();

        // `root = true` must come before the first [section].
        var firstSection = Array.FindIndex(lines, l => l.TrimStart().StartsWith('['));
        var header = firstSection < 0 ? lines : lines[..firstSection];
        if (!header.Any(l => RootTrue().IsMatch(l)))
        {
            violations.Add("the file does not start with root = true");
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#') || line.StartsWith(';') || !LowersARule().IsMatch(line))
            {
                continue;
            }

            var above = i > 0 ? lines[i - 1].TrimStart() : string.Empty;
            if (!above.StartsWith('#'))
            {
                violations.Add($"line {i + 1} lowers a rule without a # comment directly above it: {line}");
            }
        }

        return violations;
    }

    private static List<string> WorkflowViolations(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Select(l => InlineComment().Replace(l, string.Empty))
            .ToList();
        var steps = SplitSteps(lines);
        var violations = new List<string>();

        if (!steps.Any(step => step.Any(l => FormatVerify().IsMatch(l)) && StepCanFail(step)))
        {
            violations.Add("no `dotnet format ... --verify-no-changes` step");
        }

        if (!steps.Any(step => step.Any(l => BuildWithWarningsAsErrors().IsMatch(l)) && StepCanFail(step)))
        {
            violations.Add("no `dotnet build ... -p:TreatWarningsAsErrors=true` step");
        }

        return violations;
    }

    // A step starts at a `- ` line; lines before the first one form a step of their own.
    private static List<List<string>> SplitSteps(List<string> lines)
    {
        var steps = new List<List<string>> { new() };
        foreach (var line in lines)
        {
            if (StepStart().IsMatch(line))
            {
                steps.Add([]);
            }

            steps[^1].Add(line);
        }

        return steps;
    }

    // Fails the job when the command fails: no continue-on-error, no `if: false`, no `|| true` on the run line.
    private static bool StepCanFail(List<string> step) =>
        !step.Any(l => ContinueOnError().IsMatch(l) || NeverRuns().IsMatch(l) || (l.Contains("dotnet ", StringComparison.Ordinal) && l.Contains("||", StringComparison.Ordinal)));

    private static List<string> SuppressionViolations(string codeWithoutComments)
    {
        var violations = new List<string>();

        foreach (Match pragma in PragmaDisable().Matches(codeWithoutComments))
        {
            violations.Add($"warning pragma: {pragma.Value.Trim()}");
        }

        // Any use of the attribute, whatever surrounds it: `[Fact, ...]`, `[assembly: ...]`, `[module: ...]`.
        foreach (Match call in SuppressMessageCall().Matches(codeWithoutComments))
        {
            if (!NonEmptyJustification().IsMatch(BalancedArguments(codeWithoutComments, call.Index + call.Length)))
            {
                violations.Add("SuppressMessage without a Justification");
            }
        }

        return violations;
    }

    // The root props file owns the analyzer mode; anywhere else those settings are a way to weaken it quietly.
    private static List<string> ProjectViolations(string xml, bool isRootProps)
    {
        var violations = new List<string>();

        foreach (var element in XDocument.Parse(xml).Descendants().Where(e => !e.HasElements))
        {
            var name = element.Name.LocalName;
            var value = element.Value.Trim();

            if (name == NoWarnTag)
            {
                violations.Add($"{NoWarnTag} item or property");
            }
            else if (name is "RunAnalyzers" or "EnableNETAnalyzers" && value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{name} is false");
            }
            else if (!isRootProps && name is "AnalysisMode" or "AnalysisModeSecurity" or "AnalysisLevel" or "TreatWarningsAsErrors" or "WarningsNotAsErrors")
            {
                violations.Add($"{name} is set outside Directory.Build.props");
            }
            else if (isRootProps && name == "WarningsNotAsErrors" && value.Split(';').Any(v => !string.IsNullOrWhiteSpace(v) && v.Trim() is not ("$(WarningsNotAsErrors)" or "NU1900")))
            {
                violations.Add("WarningsNotAsErrors lists more than NU1900");
            }
        }

        return violations;
    }

    [GeneratedRegex(@"^\s*root\s*=\s*true\s*(?:[#;].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RootTrue();

    [GeneratedRegex(@"\bseverity\s*=\s*(none|silent|suggestion|default)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowersARule();

    [GeneratedRegex(@"\bdotnet\s+format\b.*--verify-no-changes\b")]
    private static partial Regex FormatVerify();

    [GeneratedRegex(@"\bdotnet\s+build\b.*-p:TreatWarningsAsErrors=true\b")]
    private static partial Regex BuildWithWarningsAsErrors();

    [GeneratedRegex(@"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b[^\n]*", RegexOptions.Multiline)]
    private static partial Regex PragmaDisable();

    // [M] keeps this source line from matching its own pattern when the guard scans this file.
    [GeneratedRegex(@"Suppress[M]essage(?:Attribute)?\s*\(")]
    private static partial Regex SuppressMessageCall();

    [GeneratedRegex("Justification\\s*=\\s*\"\\s*[^\"\\s]")]
    private static partial Regex NonEmptyJustification();

    [GeneratedRegex(@"\s#.*$")]
    private static partial Regex InlineComment();

    [GeneratedRegex(@"^\s*-\s")]
    private static partial Regex StepStart();

    [GeneratedRegex(@"^\s*(?:-\s+)?continue-on-error:\s*true\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueOnError();

    [GeneratedRegex(@"^\s*(?:-\s+)?if:\s*(?:\$\{\{\s*)?false\b", RegexOptions.IgnoreCase)]
    private static partial Regex NeverRuns();

    [GeneratedRegex(@"^src/EnergyTracker\.Infrastructure\.Migrations\.[^/]+/Migrations/[^/]+\.cs$")]
    private static partial Regex GeneratedMigration();

    // ---- Files

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    // EF migrations are generated (scripts/add-migration.sh) and carry their own pragmas; .editorconfig marks
    // them generated_code, so the suppression rule does not apply to them.
    private static List<string> CSharpFiles(string root) =>
        CodeRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildPath(f) && !IsGeneratedMigration(f))
            .ToList();

    // Mirrors the .editorconfig section [src/EnergyTracker.Infrastructure.Migrations.*/Migrations/*.cs].
    private static bool IsGeneratedMigration(string relativePath) => GeneratedMigration().IsMatch(relativePath);

    // The text between the opening parenthesis at `start - 1` and its match, skipping string literals so a
    // `)` or `]` inside a Justification cannot end it early.
    private static string BalancedArguments(string code, int start)
    {
        var depth = 1;
        var i = start;
        while (i < code.Length && depth > 0)
        {
            switch (code[i])
            {
                case '"':
                    i++;
                    while (i < code.Length && code[i] != '"')
                    {
                        i += code[i] == '\\' ? 2 : 1;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
            }

            i++;
        }

        return code[start..Math.Min(i, code.Length)];
    }

    private static bool IsBuildPath(string relativePath) =>
        relativePath.Split('/').Any(segment => segment is "obj" or "bin" or "node_modules");

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
