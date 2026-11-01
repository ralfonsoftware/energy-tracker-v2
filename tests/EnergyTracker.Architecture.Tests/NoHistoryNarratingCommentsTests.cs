using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.19 (audit R2): a comment states why, in one to three lines. History (which story, which review
// round) lives in git and long reasoning in docs/adr. This guard covers the files the story touched and keeps
// them that way; it does not claim the rest of the codebase is clean (see deferred-work.md).
public partial class NoHistoryNarratingCommentsTests
{
    private const int MaxPlainRun = 3;
    private const int MaxPointerRun = 6;

    private static readonly string[] ScopeFiles =
    [
        "src/EnergyTracker.Api/Program.cs",
        "src/EnergyTracker.Api/StartupFailureExit.cs",
        "src/EnergyTracker.Api/HouseholdRequiredFilter.cs",
        "src/EnergyTracker.Api/Endpoints/TaggingScaffoldEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/RoomEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/PowerPointEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/DeviceEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/SmartPlugReadingEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/MeterRegressionPromptEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdExportEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/TariffEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/MeterReadingEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/StatusEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/EventEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/JobEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdInviteEndpoints.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdMemberEndpoints.cs",
        "src/EnergyTracker.Api/HouseholdRequiredMetadata.cs",
        "src/EnergyTracker.Api/Endpoints/TaggingScaffoldContracts.cs",
    ];

    private static readonly string[] ScopeFolders =
    [
        "src/EnergyTracker.Api/Composition",
        "web/src/components/tagging-scaffold",
    ];

    // The scope that Program.cs and the Infrastructure repository add by glob.
    private const string RepositoryFolder = "src/EnergyTracker.Infrastructure/Adapters";
    private const string RepositoryPattern = "SmartPlugImportRepository*.cs";

    [GeneratedRegex(@"Story \d+\.\d+|code review|review fix|\bPass \d|review finding|\bRound[- ]\d|\bAC #\d|\b[Aa]udit [A-Z]\d", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryPhrase();

    [GeneratedRegex(@"\bAD-\d+|\bADR \d{4}")]
    private static partial Regex PointerPhrase();

    [Fact]
    public void Comments_in_the_touched_files_are_short_and_do_not_narrate_history()
    {
        var root = FindRepoRoot();
        var problems = Files(root)
            .SelectMany(f => Problems(File.ReadAllLines(Path.Combine(root, f)), f))
            .ToList();

        problems.ShouldBeEmpty(
            "Trim to one to three lines of why and move longer reasoning to docs/adr (Story 11.19, audit R2): " + string.Join("; ", problems));
    }

    [Fact]
    public void The_scan_covers_the_touched_files()
    {
        var files = Files(FindRepoRoot());

        files.Count.ShouldBeGreaterThan(30, "the scan found too few files: did the layout move? Update this guard with it.");
        files.ShouldContain("src/EnergyTracker.Api/Program.cs");
        files.ShouldContain(f => f.StartsWith("src/EnergyTracker.Api/Composition/", StringComparison.Ordinal));
        files.ShouldContain(f => f.Contains("SmartPlugImportRepository.Cleanup.cs", StringComparison.Ordinal));
        files.ShouldContain(f => f.EndsWith("tagging-scaffold-manager.tsx", StringComparison.Ordinal));
        files.ShouldNotContain(f => f.Contains(".test.", StringComparison.Ordinal));
    }

    // Non-vacuous: the run counter and the phrase matcher are proven on known text.
    [Fact]
    public void The_scan_itself_detects_a_long_run_and_a_history_phrase()
    {
        string[] longRun = ["    // one", "    // two", "    // three", "    // four", "    var x = 1;"];
        Problems(longRun, "a.cs").ShouldHaveSingleItem();

        string[] shortRun = ["    // one", "    // two", "    // three", "    var x = 1;"];
        Problems(shortRun, "a.cs").ShouldBeEmpty();

        string[] pointerRun = ["// AD-24: one", "// two", "// three", "// four", "// ADR 0003", "// six", "var x = 1;"];
        Problems(pointerRun, "a.cs").ShouldBeEmpty();

        string[] longPointerRun = ["// AD-24: one", "// two", "// three", "// four", "// five", "// six", "// seven", "var x = 1;"];
        Problems(longPointerRun, "a.cs").ShouldHaveSingleItem();

        Problems(["// fixed in Code Review, Story 7.2 Pass 1", "var x = 1;"], "a.cs").ShouldHaveSingleItem();
        Problems(["var x = 1; // Round-3 review fix"], "a.cs").ShouldHaveSingleItem();
        Problems(["// Story 7.2 added this", "var x = 1;"], "a.cs").ShouldHaveSingleItem();
        Problems(["// per AC #6", "var x = 1;"], "a.cs").ShouldHaveSingleItem();
        Problems(["// see audit R2", "var x = 1;"], "a.cs").ShouldHaveSingleItem();
        Problems(["/// <summary>Added in Story 11.19.</summary>", "var x = 1;"], "a.cs").ShouldHaveSingleItem();
        Problems(["var url = \"https://example.test/Story 1.2\";", "var y = 1; // fine"], "a.cs").ShouldBeEmpty();
        Problems(["// see https://example.test/docs", "var y = 1;"], "a.cs").ShouldBeEmpty();
        Problems(["var s = \"// Round-3 review fix\";"], "a.cs").ShouldBeEmpty();

        // A pointer is AD-<n> or ADR <nnnn>, not any word that contains AD- or ADR.
        string[] fakePointer = ["// BROAD-ish one", "// two", "// three", "// four", "var x = 1;"];
        Problems(fakePointer, "a.cs").ShouldHaveSingleItem();
        string[] adrPointer = ["// one", "// two", "// three", "// four", "// see ADR 0003", "var x = 1;"];
        Problems(adrPointer, "a.cs").ShouldBeEmpty();

        Problems(["/// <summary>summary line</summary>", "/// <remarks>and more</remarks>", "/// three", "/// four", "/// five"], "a.cs").ShouldBeEmpty();
    }

    private static List<string> Problems(string[] lines, string file)
    {
        var problems = new List<string>();
        var runStart = -1;
        for (var i = 0; i <= lines.Length; i++)
        {
            var isComment = i < lines.Length && IsLineComment(lines[i]);
            if (isComment && runStart < 0)
            {
                runStart = i;
            }
            else if (!isComment && runStart >= 0)
            {
                var run = lines[runStart..i];
                var pointer = run.Any(l => PointerPhrase().IsMatch(l));
                var allowed = pointer ? MaxPointerRun : MaxPlainRun;
                if (run.Length > allowed)
                {
                    problems.Add($"{file}:{runStart + 1} ({run.Length} comment lines)");
                }

                runStart = -1;
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var comment = CommentText(lines[i]);
            if (comment is not null && HistoryPhrase().IsMatch(comment))
            {
                problems.Add($"{file}:{i + 1} (history phrase)");
            }
        }

        return problems;
    }

    private static bool IsLineComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal);
    }

    // The comment of a line (`//` and `///` included), or null. A `//` inside a string literal or right after a
    // colon (a URL such as https://host) does not start a comment.
    private static string? CommentText(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'' or '`')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/' && (i == 0 || line[i - 1] != ':'))
            {
                return line[i..];
            }
        }

        return null;
    }

    private static List<string> Files(string root)
    {
        var files = new List<string>(ScopeFiles);
        foreach (var directory in ScopeFolders.Append(RepositoryFolder))
        {
            Directory.Exists(Path.Combine(root, directory)).ShouldBeTrue($"{directory} does not exist: did the layout move? Update this guard with it.");
        }

        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, RepositoryFolder), RepositoryPattern).Select(f => Relative(root, f)));
        foreach (var folder in ScopeFolders)
        {
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
                .Where(f => !f.Contains(".test.", StringComparison.Ordinal))
                .Select(f => Relative(root, f)));
        }

        return [.. files.Order(StringComparer.Ordinal)];
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

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
