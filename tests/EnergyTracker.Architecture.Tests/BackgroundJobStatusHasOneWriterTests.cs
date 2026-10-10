using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-6 (amended 2026-10-10): every BackgroundJob status change goes through IBackgroundJobLifecycle
// as one conditional update, so a terminal status is never overwritten. This guard makes "no other
// code writes BackgroundJob.Status" executable. Source-text scan, same convention as
// HouseholdExportReaderDoesNotBypassTenantIsolationTests.
public partial class BackgroundJobStatusHasOneWriterTests
{
    private const string LifecycleFile = "BackgroundJobLifecycle.cs";
    private const string EnqueueRecorderFile = "BackgroundJobEnqueueRecorder.cs";

    // Three shapes write a job's Status: `x.Status = v` (any value, including a variable),
    // `SetProperty(j => j.Status, v)` (any value) and an object initializer `Status = BackgroundJobStatus.X`.
    // The first two are only inspected in files that mention BackgroundJob at all, so an unrelated
    // entity's Status in another file is never flagged; the initializer is matched by its enum literal.
    [GeneratedRegex(@"BackgroundJobStatus\.\w+")]
    private static partial Regex StatusValueRegex();

    [GeneratedRegex(@"\bStatus\s*=(?![=>])")]
    private static partial Regex StatusAssignmentRegex();

    [GeneratedRegex(@"\.Status\s*=(?![=>])")]
    private static partial Regex MemberStatusAssignmentRegex();

    [GeneratedRegex(@"SetProperty\(\s*\w+\s*=>\s*\w+\.Status\b")]
    private static partial Regex SetPropertyStatusRegex();

    [Fact]
    public void Only_BackgroundJobLifecycle_assigns_BackgroundJob_Status_under_src()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            violations.AddRange(FindViolations(Path.GetFileName(file), CodeLines(file).ToList())
                .Select(line => $"{Path.GetRelativePath(repoRoot, file)}: {line.Trim()}"));
        }

        violations.ShouldBeEmpty(
            "Only BackgroundJobLifecycle may change BackgroundJob.Status (AD-6); the enqueue recorder may set the initial Queued only:\n" +
            string.Join('\n', violations));
    }

    [Theory]
    [InlineData("Worker.cs", "job.Status = newStatus;", true)]
    [InlineData("Worker.cs", "job.Status = BackgroundJobStatus.Failed;", true)]
    [InlineData("Worker.cs", ".SetProperty(j => j.Status, status)", true)]
    [InlineData("Worker.cs", "Status = BackgroundJobStatus.Failed,", true)]
    [InlineData("Worker.cs", "var url = \"http://x\"; job.Status = s;", true)]
    [InlineData("Worker.cs", "if (job.Status == BackgroundJobStatus.Failed) { }", false)]
    [InlineData("Worker.cs", "Func<BackgroundJob, bool> f = j => j.Status == s;", false)]
    [InlineData("BackgroundJobLifecycle.cs", "job.Status = newStatus;", false)]
    [InlineData("BackgroundJobEnqueueRecorder.cs", "Status = BackgroundJobStatus.Queued,", false)]
    [InlineData("BackgroundJobEnqueueRecorder.cs", "Status = BackgroundJobStatus.Failed,", true)]
    public void The_scanner_flags_status_writes_in_every_shape(string fileName, string line, bool flagged)
    {
        // A file only has to mention BackgroundJob (here via a type reference) for the variable shapes to apply.
        var lines = new[] { "BackgroundJob? current;", CodeLine(line) };
        FindViolations(fileName, lines).Any().ShouldBe(flagged);
    }

    private static IEnumerable<string> FindViolations(string fileName, IReadOnlyList<string> lines)
    {
        if (fileName == LifecycleFile)
        {
            yield break;
        }

        // `lines` are comment-stripped, so a file that only talks about BackgroundJob in comments is not inspected.
        var mentionsBackgroundJob = lines.Any(line => line.Contains("BackgroundJob", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            var literalInitializer = StatusValueRegex().IsMatch(line) && StatusAssignmentRegex().IsMatch(line);
            var variableWrite = mentionsBackgroundJob &&
                                (MemberStatusAssignmentRegex().IsMatch(line) || SetPropertyStatusRegex().IsMatch(line));
            if (!literalInitializer && !variableWrite)
            {
                continue;
            }

            // The enqueue recorder may only insert the initial Queued row.
            if (fileName == EnqueueRecorderFile && line.Contains("Status = BackgroundJobStatus.Queued", StringComparison.Ordinal))
            {
                continue;
            }

            yield return line;
        }
    }

    [Fact]
    public void The_lifecycle_port_lives_in_Application_Ports_and_the_adapter_in_Infrastructure()
    {
        var repoRoot = FindRepoRoot();
        var port = Path.Combine(repoRoot, "src/EnergyTracker.Application/Ports/IBackgroundJobLifecycle.cs");
        File.Exists(port).ShouldBeTrue($"Expected {port}");
        File.ReadAllText(port).ShouldContain("namespace EnergyTracker.Application.Ports;");

        File.Exists(Path.Combine(repoRoot, "src/EnergyTracker.Infrastructure/Adapters", LifecycleFile)).ShouldBeTrue();
    }

    [Fact]
    public void The_startup_sweep_never_bypasses_the_AD3_tenant_isolation_filter()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src/EnergyTracker.Infrastructure/Adapters/InProcessJobStartupSweep.cs");
        File.Exists(path).ShouldBeTrue($"Expected {path}");

        foreach (var line in CodeLines(path))
        {
            line.Contains("IgnoreQueryFilters", StringComparison.Ordinal).ShouldBeFalse($"startup sweep uses IgnoreQueryFilters: {line}");
            line.Contains("FromSql", StringComparison.Ordinal).ShouldBeFalse($"startup sweep uses raw SQL: {line}");
        }
    }

    private static IEnumerable<string> CodeLines(string path) => File.ReadAllLines(path).Select(CodeLine);

    // Drops a trailing // comment, but not a // inside a string literal ("http://...").
    private static string CodeLine(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '/' && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
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
