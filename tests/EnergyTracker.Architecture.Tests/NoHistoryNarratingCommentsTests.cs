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
    ];

    private static readonly string[] ScopeFolders =
    [
        "src/EnergyTracker.Api/Composition",
        "web/src/components/tagging-scaffold",
    ];

    // The scope that Program.cs and the Infrastructure repository add by glob.
    private const string RepositoryFolder = "src/EnergyTracker.Infrastructure/Adapters";
    private const string RepositoryPattern = "SmartPlugImportRepository*.cs";

    [GeneratedRegex(@"Story \d+\.\d+ review|code review|review fix|\bPass \d|review finding|\bRound[- ]\d", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryPhrase();

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
                var pointer = run.Any(l => l.Contains("AD-", StringComparison.Ordinal) || l.Contains("ADR", StringComparison.Ordinal));
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

    private static string? CommentText(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        if (index < 0 || line.AsSpan(index).StartsWith("///", StringComparison.Ordinal))
        {
            return null;
        }

        return line[index..];
    }

    private static List<string> Files(string root)
    {
        var files = new List<string>(ScopeFiles);
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
