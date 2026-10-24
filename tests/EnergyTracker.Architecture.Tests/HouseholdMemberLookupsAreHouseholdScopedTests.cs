using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-26 / AD-3 (Story 11.9, AC #3, #10): HouseholdMember and HouseholdInvite are the two documented
// query-filter exceptions, so nothing protects a by-id access to them except the explicit HouseholdId
// predicate in the statement itself. This text scan makes that executable: only an allow-list of files
// may touch the two DbSets, no bypass appears on them, and every by-id access carries HouseholdId.
// A source-text scan, like HouseholdExportReaderDoesNotBypassTenantIsolationTests.
public partial class HouseholdMemberLookupsAreHouseholdScopedTests
{
    // File name -> (DbSet it may touch, why). Adding a new file that queries either set forces a conscious edit here.
    private static readonly (string File, string Identifier, string Reason)[] Allowed =
    [
        ("EnergyTrackerDbContext.cs", "HouseholdMembers", "declares the DbSet"),
        ("EnergyTrackerDbContext.cs", "HouseholdInvites", "declares the DbSet"),
        ("HouseholdMembershipDbContext.cs", "HouseholdMembers", "declares the DbSet for the resolver's own-row lookup"),
        ("HouseholdRepository.cs", "HouseholdMembers", "the one scoped by-id lookup (FindMemberInHouseholdAsync), the principal's own row, the Household's member list and count"),
        ("HouseholdRepository.cs", "HouseholdInvites", "token-addressed lookup plus the Household-scoped list, revoke and expire-all"),
        ("HouseholdMembershipResolver.cs", "HouseholdMembers", "resolves the principal's own row by issuer and subject"),
        ("HouseholdExportReader.cs", "HouseholdMembers", "explicit HouseholdId match on every export read"),
        ("BackgroundJobRepository.cs", "HouseholdMembers", "by-id batch scoped to the Household (Story 11.9)"),
    ];

    private static readonly string[] BypassTokens = [".Find(", ".FindAsync(", "IgnoreQueryFilters", "FromSql"];

    [GeneratedRegex(@"(?:[A-Za-z]*[Cc]ontext|[Dd]b)\.(HouseholdMembers|HouseholdInvites)\b|Set<(HouseholdMember|HouseholdInvite)>")]
    private static partial Regex DbSetAccess();

    // A predicate on the entity's own Id: "Id ==" or ".Contains(x.Id)" not preceded by a letter (so HouseholdId does not match).
    [GeneratedRegex(@"(?<![A-Za-z])Id\s*==|Contains\([A-Za-z]+\.Id\)")]
    private static partial Regex ByIdPredicate();

    // A lookup by the invite's credential: "Token" or "TokenHash" as an identifier, not the tail of "cancellationToken".
    [GeneratedRegex(@"(?<![A-Za-z])Token(Hash)?\b")]
    private static partial Regex TokenAddressed();

    [GeneratedRegex(@"FindMemberInHouseholdAsync\(Guid householdId, Guid memberId[^;]*?HouseholdMembers[^;]*?HouseholdId\s*==[^;]*?(?<![A-Za-z])Id\s*==[^;]*;", RegexOptions.Singleline)]
    private static partial Regex TheScopedLookup();

    private record SourceFile(string Name, string RelativePath, string Code);

    private static List<SourceFile> Sources()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(src, path);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                return !segments.Contains("obj") && !segments.Contains("bin") && !segments.Any(s => s.StartsWith("EnergyTracker.Infrastructure.Migrations", StringComparison.Ordinal));
            })
            .Select(path => new SourceFile(Path.GetFileName(path), Path.GetRelativePath(root, path), StripComments(File.ReadAllText(path))))
            .ToList();
    }

    private static string StripComments(string code)
    {
        var withoutBlocks = Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return string.Join('\n', withoutBlocks.Split('\n').Select(line =>
        {
            var index = line.IndexOf("//", StringComparison.Ordinal);
            return index >= 0 ? line[..index] : line;
        }));
    }

    private static IEnumerable<string> StatementsTouching(SourceFile file, string dbSet) =>
        file.Code.Split(';').Where(statement =>
            DbSetAccess().Matches(statement).Any(m => m.Groups[1].Value == dbSet || m.Groups[2].Value == dbSet.TrimEnd('s')));

    [Fact]
    public void Only_the_allow_listed_files_touch_the_HouseholdMembers_and_HouseholdInvites_sets()
    {
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            foreach (var identifier in new[] { "HouseholdMembers", "HouseholdInvites" })
            {
                if (StatementsTouching(file, identifier).Any() && !Allowed.Any(a => a.File == file.Name && a.Identifier == identifier))
                {
                    offenders.Add($"{file.RelativePath} touches {identifier}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "These files query an AD-3 query-filter exception without being allow-listed. Scope the lookup by HouseholdId inside HouseholdRepository, or add the file to Allowed with a reason (AD-26).");
    }

    [Fact]
    public void No_bypass_appears_on_the_HouseholdMembers_or_HouseholdInvites_sets()
    {
        var offenders = new List<string>();
        foreach (var file in Sources())
        {
            foreach (var statement in StatementsTouching(file, "HouseholdMembers").Concat(StatementsTouching(file, "HouseholdInvites")))
            {
                offenders.AddRange(BypassTokens
                    .Where(token => statement.Contains(token, StringComparison.Ordinal))
                    .Select(token => $"{file.RelativePath}: '{token}' on an exempt set"));
            }
        }

        offenders.ShouldBeEmpty("Find/FindAsync/IgnoreQueryFilters/FromSql on HouseholdMembers or HouseholdInvites is forbidden (AD-3, AD-26).");
    }

    [Fact]
    public void Every_by_id_member_access_carries_HouseholdId_and_the_repository_has_exactly_one_scoped_lookup()
    {
        var sources = Sources();
        var byIdMemberStatements = sources
            .SelectMany(file => StatementsTouching(file, "HouseholdMembers").Select(statement => (File: file, Statement: statement)))
            .Where(x => ByIdPredicate().IsMatch(x.Statement))
            .ToList();

        byIdMemberStatements.ShouldNotBeEmpty("the scan must find the by-id member accesses it guards");
        byIdMemberStatements
            .Where(x => !x.Statement.Contains("HouseholdId", StringComparison.Ordinal))
            .Select(x => x.File.RelativePath)
            .ShouldBeEmpty("A by-id lookup of HouseholdMembers without a HouseholdId predicate lets one Household reach another's member (AD-26).");

        var repository = sources.Single(f => f.Name == "HouseholdRepository.cs");
        StatementsTouching(repository, "HouseholdMembers").Count(s => ByIdPredicate().IsMatch(s)).ShouldBe(1);
        TheScopedLookup().Matches(repository.Code).Count.ShouldBe(1, "FindMemberInHouseholdAsync must be the one by-id member lookup, with HouseholdId and Id in its predicate.");
    }

    [Fact]
    public void Every_HouseholdInvites_statement_that_is_not_token_addressed_or_an_insert_carries_HouseholdId()
    {
        var statements = Sources()
            .SelectMany(file => StatementsTouching(file, "HouseholdInvites").Select(statement => (File: file, Statement: statement)))
            .Where(x => !x.Statement.Contains("DbSet<", StringComparison.Ordinal))
            .ToList();

        statements.ShouldNotBeEmpty("the scan must find the invite accesses it guards");
        statements
            .Where(x => !TokenAddressed().IsMatch(x.Statement)
                        && !x.Statement.Contains(".AddAsync(", StringComparison.Ordinal)
                        && !x.Statement.Contains("HouseholdId", StringComparison.Ordinal))
            .Select(x => x.File.RelativePath)
            .ShouldBeEmpty("An invite access by id or list without a HouseholdId predicate lets one Household see or revoke another's invite (AD-26, AC #10).");
    }

    // Non-vacuity: a rename must not silently empty the guard.
    [Fact]
    public void Every_allow_listed_file_still_exists_and_still_touches_what_it_is_allowed_to()
    {
        var sources = Sources();

        foreach (var (file, identifier, reason) in Allowed)
        {
            var match = sources.SingleOrDefault(s => s.Name == file);
            match.ShouldNotBeNull($"{file} ({reason}) no longer exists; update the allow-list.");
            (StatementsTouching(match, identifier).Any() || match.Code.Contains(identifier, StringComparison.Ordinal))
                .ShouldBeTrue($"{file} no longer references {identifier} ({reason}); update the allow-list.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
    }
}
