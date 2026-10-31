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
    public void No_project_file_lists_NoWarn()
    {
        var root = FindRepoRoot();
        // The solution's projects only: spikes/ holds throwaway projects that are not in EnergyTracker.sln.
        var files = CodeRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.csproj", SearchOption.AllDirectories))
            .Concat([Path.Combine(root, PropsPath)])
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildPath(f))
            .ToList();

        files.Count.ShouldBeGreaterThan(5, "the scan found almost no project files: did the layout move? Update this guard with it.");

        var violations = files.Where(f => ProjectViolations(File.ReadAllText(Path.Combine(root, f))).Count != 0).ToList();

        violations.ShouldBeEmpty("a NoWarn item hides diagnostics for a whole project: lower the rule in .editorconfig with a comment instead (Story 11.18). " + string.Join("; ", violations));
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

        // Inside a comment it is not code.
        SuppressionViolations(CSharpSource.StripComments($"// {Hash}pragma warning disable CA1848\n")).ShouldBeEmpty();
        SuppressionViolations(CSharpSource.StripComments($"/* [{Suppress}(\"Design\", \"CA1001\")] */ class A;")).ShouldBeEmpty();
        SuppressionViolations(CSharpSource.StripComments($"// [{Suppress}(\"Design\", \"CA1001\")]\nclass A;")).ShouldBeEmpty();
    }

    [Fact]
    public void The_project_rule_reports_NoWarn_and_ignores_comments()
    {
        ProjectViolations($"<Project><PropertyGroup><{NoWarnTag}>CA1848</{NoWarnTag}></PropertyGroup></Project>").Count.ShouldBe(1);
        ProjectViolations($"<Project><ItemGroup><{NoWarnTag} Include=\"CA1848\" /></ItemGroup></Project>").Count.ShouldBe(1);
        ProjectViolations($"<Project><!-- <{NoWarnTag}>CA1848</{NoWarnTag}> --><PropertyGroup /></Project>").ShouldBeEmpty();
        ProjectViolations("<Project><PropertyGroup><WarningsNotAsErrors>NU1900</WarningsNotAsErrors></PropertyGroup></Project>").ShouldBeEmpty();
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
        var code = string.Join('\n', yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        var lines = code.Split('\n');
        var violations = new List<string>();

        if (!lines.Any(l => FormatVerify().IsMatch(l)))
        {
            violations.Add("no `dotnet format ... --verify-no-changes` step");
        }

        if (!lines.Any(l => BuildWithWarningsAsErrors().IsMatch(l)))
        {
            violations.Add("no `dotnet build ... -p:TreatWarningsAsErrors=true` step");
        }

        return violations;
    }

    private static List<string> SuppressionViolations(string codeWithoutComments)
    {
        var violations = new List<string>();

        foreach (Match pragma in PragmaDisable().Matches(codeWithoutComments))
        {
            violations.Add($"warning pragma: {pragma.Value.Trim()}");
        }

        foreach (Match attribute in SuppressMessageAttribute().Matches(codeWithoutComments))
        {
            if (!NonEmptyJustification().IsMatch(attribute.Value))
            {
                violations.Add("SuppressMessage without a Justification");
            }
        }

        return violations;
    }

    private static List<string> ProjectViolations(string xml) =>
        XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName == NoWarnTag)
            .Select(e => $"{NoWarnTag} item or property")
            .ToList();

    [GeneratedRegex(@"^\s*root\s*=\s*true\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RootTrue();

    [GeneratedRegex(@"\bseverity\s*=\s*(none|silent|suggestion)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowersARule();

    [GeneratedRegex(@"\bdotnet\s+format\b.*--verify-no-changes\b")]
    private static partial Regex FormatVerify();

    [GeneratedRegex(@"\bdotnet\s+build\b.*-p:TreatWarningsAsErrors=true\b")]
    private static partial Regex BuildWithWarningsAsErrors();

    [GeneratedRegex(@"^[ \t]*#[ \t]*pragma[ \t]+warning[ \t]+disable\b[^\n]*", RegexOptions.Multiline)]
    private static partial Regex PragmaDisable();

    [GeneratedRegex(@"\[\s*(?:assembly\s*:\s*)?(?:System\.Diagnostics\.CodeAnalysis\.)?SuppressMessage(?:Attribute)?\b[^\]]*\]")]
    private static partial Regex SuppressMessageAttribute();

    [GeneratedRegex("Justification\\s*=\\s*\"[^\"]+\"")]
    private static partial Regex NonEmptyJustification();

    // ---- Files

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    // EF migrations are generated (scripts/add-migration.sh) and carry their own pragmas; .editorconfig marks
    // them generated_code, so the suppression rule does not apply to them.
    private static List<string> CSharpFiles(string root) =>
        CodeRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildPath(f) && !f.Split('/').Contains("Migrations"))
            .ToList();

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
