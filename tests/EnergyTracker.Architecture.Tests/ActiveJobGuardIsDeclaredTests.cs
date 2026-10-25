using System.Text.RegularExpressions;
using EnergyTracker.Application;
using EnergyTracker.Domain;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.14 (audit C10, AD-6): "at most one Queued/Processing RestoreHouseholdData and one
// CleanUpSmartPlugImportJobs row per Household" is a database constraint, a filtered unique index that
// exists only as raw SQL in each provider's migration (the filter is dialect text and the model is shared
// by both providers, AD-2; SmartPlugReadingConfiguration.cs is the precedent). Prose rules die, so this
// guard makes the declaration executable: text scans, comments stripped, non-vacuous (each scanned file must
// exist and each assertion must have found its anchor).
public partial class ActiveJobGuardIsDeclaredTests
{
    private const string IndexName = "UX_BackgroundJobs_ActiveExclusive";
    private const string MigrationSuffix = "_AddActiveJobGuard.cs";
    private const string Why = "Story 11.14 / AD-6 (one active restore or cleanup per Household, enforced by the database) / AD-2 (dialect SQL lives in migrations only)";

    [GeneratedRegex(@"CREATE\s+UNIQUE\s+(?:NONCLUSTERED\s+)?INDEX[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex CreateUniqueIndexStatementRegex();

    [GeneratedRegex(@"\bJobType\W*\s+IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex JobTypeListRegex();

    [GeneratedRegex(@"\bStatus\W*\s+IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusListRegex();

    [GeneratedRegex(@"^N?'(.*)'$")]
    private static partial Regex StringLiteralRegex();

    private static readonly string[] MigrationProjects =
    [
        "src/EnergyTracker.Infrastructure.Migrations.Postgres",
        "src/EnergyTracker.Infrastructure.Migrations.SqlServer",
    ];

    [Fact]
    public void Both_provider_migrations_create_the_index_for_exactly_the_guarded_job_types_and_the_active_statuses()
    {
        var repoRoot = FindRepoRoot();
        var activeStatuses = ActiveStatusIntegers();

        foreach (var project in MigrationProjects)
        {
            var migration = FindMigration(repoRoot, project);
            var text = string.Join('\n', CodeLines(migration));

            var statements = CreateUniqueIndexStatementRegex().Matches(text).Select(m => m.Value)
                .Where(s => s.Contains(IndexName, StringComparison.Ordinal)).ToList();
            statements.ShouldNotBeEmpty($"{Rel(repoRoot, migration)} has no CREATE UNIQUE INDEX statement for {IndexName}. {Why}");

            var guardedTypes = new HashSet<string>();
            foreach (var statement in statements)
            {
                var statuses = ParseList(StatusListRegex(), statement, "Status", migration);
                statuses.Select(int.Parse).ToHashSet().ShouldBe(activeStatuses, ignoreOrder: true,
                    $"{Rel(repoRoot, migration)}: the index filter must list exactly the integers of BackgroundJobStatus.Queued and Processing. {Why}");

                foreach (var type in ParseList(JobTypeListRegex(), statement, "JobType", migration))
                {
                    guardedTypes.Add(Unquote(type));
                }
            }

            guardedTypes.ShouldBe(JobTypes.ExclusivePerHousehold.ToHashSet(), ignoreOrder: true,
                $"{Rel(repoRoot, migration)}: the index must cover exactly JobTypes.ExclusivePerHousehold. {Why}");
        }
    }

    [Fact]
    public void A_new_BackgroundJobStatus_fails_until_someone_decides_whether_it_is_active_for_the_index()
    {
        var nonTerminal = Enum.GetValues<BackgroundJobStatus>()
            .Where(s => s is not (BackgroundJobStatus.Completed or BackgroundJobStatus.Failed))
            .ToHashSet();

        nonTerminal.ShouldBe([BackgroundJobStatus.Queued, BackgroundJobStatus.Processing], ignoreOrder: true,
            customMessage: "BackgroundJobStatus gained a member. Decide whether it counts as active (then extend the filter of the " +
            $"{IndexName} index in both provider migrations with a new migration and this test) or terminal. {Why}");
    }

    [Fact]
    public void BackgroundJobConfiguration_declares_no_filter_and_no_unique_index()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src/EnergyTracker.Infrastructure/Configurations/BackgroundJobConfiguration.cs");
        File.Exists(path).ShouldBeTrue($"Expected {path}");
        var lines = CodeLines(path).ToList();

        // Anchor: the scan found the file's own index declaration, so an empty or moved file cannot pass.
        lines.ShouldContain(l => l.Contains("HasIndex(", StringComparison.Ordinal) && l.Contains("JobType", StringComparison.Ordinal),
            "BackgroundJobConfiguration.cs no longer declares its (HouseholdId, JobType, CreatedAtUtc) index; update this guard.");

        foreach (var line in lines)
        {
            line.Contains("HasFilter", StringComparison.Ordinal).ShouldBeFalse(
                $"BackgroundJobConfiguration.cs declares a filter ({line.Trim()}). A filter is dialect SQL and this class is shared by both providers: " +
                $"declare the index only as raw SQL in each provider's migration, as SmartPlugReadingConfiguration.cs explains. {Why}");
            line.Contains("IsUnique", StringComparison.Ordinal).ShouldBeFalse(
                $"BackgroundJobConfiguration.cs declares a unique index ({line.Trim()}); the active-job guard is migration-only SQL. See SmartPlugReadingConfiguration.cs. {Why}");
        }
    }

    [Fact]
    public void The_model_snapshots_do_not_know_the_index()
    {
        var repoRoot = FindRepoRoot();
        foreach (var project in MigrationProjects)
        {
            var snapshot = Path.Combine(repoRoot, project, "Migrations", "EnergyTrackerDbContextModelSnapshot.cs");
            File.Exists(snapshot).ShouldBeTrue($"Expected {snapshot}");
            File.ReadAllText(snapshot).ShouldNotContain(IndexName,
                customMessage: $"{Rel(repoRoot, snapshot)} mentions the index: it must exist only as raw SQL in the migration. {Why}");
        }
    }

    [Fact]
    public void Only_BackgroundJobEnqueueRecorder_throws_ActiveJobAlreadyExistsException()
    {
        var repoRoot = FindRepoRoot();
        var throwers = new List<string>();
        foreach (var file in SourceFiles(repoRoot))
        {
            if (CodeLines(file).Any(ConstructsTheException))
            {
                throwers.Add(Path.GetFileName(file));
            }
        }

        throwers.ShouldBe(["BackgroundJobEnqueueRecorder.cs"],
            $"Only the enqueue recorder may construct ActiveJobAlreadyExistsException: it is the one place that knows the row was rejected by the index. {Why}");
    }

    [Theory]
    [InlineData("throw new ActiveJobAlreadyExistsException(a, b);", true)]
    [InlineData("var e = new ActiveJobAlreadyExistsException(a, b);", true)]
    [InlineData("catch (ActiveJobAlreadyExistsException ex)", false)]
    [InlineData("public class ActiveJobAlreadyExistsException(string jobType, Guid id) : Exception", false)]
    [InlineData("// throw new ActiveJobAlreadyExistsException(a, b);", false)]
    public void The_thrower_scan_recognises_construction_and_ignores_catches_and_comments(string line, bool constructs) =>
        ConstructsTheException(CodeLine(line)).ShouldBe(constructs);

    [Theory]
    [InlineData("CREATE UNIQUE INDEX \"UX_BackgroundJobs_ActiveExclusive\" ON \"BackgroundJobs\" (\"HouseholdId\") WHERE \"JobType\" IN ('A', 'B') AND \"Status\" IN (0, 3);", "A,B", "0,3")]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX [UX_BackgroundJobs_ActiveExclusive] ON [BackgroundJobs] ([HouseholdId]) WHERE [JobType] IN (N'A', N'B') AND [Status] IN (0, 3);", "A,B", "0,3")]
    public void The_filter_parsers_read_both_dialects(string statement, string types, string statuses)
    {
        ParseList(JobTypeListRegex(), statement, "JobType", "sample").Select(Unquote).ShouldBe(types.Split(','));
        ParseList(StatusListRegex(), statement, "Status", "sample").ShouldBe(statuses.Split(','));
    }

    private static bool ConstructsTheException(string line) =>
        line.Contains("new ActiveJobAlreadyExistsException(", StringComparison.Ordinal);

    // 'Name' (Postgres) or N'Name' (SQL Server).
    private static string Unquote(string literal)
    {
        var match = StringLiteralRegex().Match(literal);
        match.Success.ShouldBeTrue($"Expected a quoted SQL string literal, got: {literal}. {Why}");
        return match.Groups[1].Value;
    }

    private static HashSet<int> ActiveStatusIntegers() => [(int)BackgroundJobStatus.Queued, (int)BackgroundJobStatus.Processing];

    // The parsed values of the first `<column> IN (...)` list; an absent list fails loudly instead of passing.
    private static string[] ParseList(Regex regex, string statement, string column, string source)
    {
        var match = regex.Match(statement);
        match.Success.ShouldBeTrue($"{source}: the CREATE UNIQUE INDEX statement has no `{column} IN (...)` filter list. {Why}");
        return match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FindMigration(string repoRoot, string project)
    {
        var directory = Path.Combine(repoRoot, project, "Migrations");
        Directory.Exists(directory).ShouldBeTrue($"Expected {directory}");
        var matches = Directory.GetFiles(directory, "*.cs").Where(f => f.EndsWith(MigrationSuffix, StringComparison.Ordinal)).ToList();
        matches.Count.ShouldBe(1, $"{project} must contain exactly one *{MigrationSuffix} (the AddActiveJobGuard migration). {Why}");
        return matches[0];
    }

    private static IEnumerable<string> SourceFiles(string repoRoot)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string Rel(string repoRoot, string path) => Path.GetRelativePath(repoRoot, path);

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
