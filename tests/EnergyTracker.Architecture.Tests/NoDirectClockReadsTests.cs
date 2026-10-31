using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.17 (audit C6): time comes from an injected TimeProvider, so expiry and window logic can be
// tested against a controllable clock. A source-text scan (comments stripped), the same convention as
// HouseholdImportIsStreamedTests, so a later change cannot quietly bring back a direct clock read.
// Architecture.Tests references only Application and Domain, so the scan is by text on purpose.
public class NoDirectClockReadsTests
{
    private const string CompositionRoot = "src/EnergyTracker.Api/Program.cs";

    private static readonly string[] ClockReads =
    [
        "DateTimeOffset.UtcNow",
        "DateTimeOffset.Now",
        "DateTime.UtcNow",
        "DateTime.Now",
        "DateTime.Today",
    ];

    // `new X(TimeProvider.System)` in a use case is the same hole as a direct read.
    private const string SystemClock = "TimeProvider.System";

    private static readonly string[] Forbidden = [.. ClockReads, SystemClock];

    [Fact]
    public void No_file_in_src_reads_the_clock_directly_outside_the_composition_root()
    {
        var root = FindRepoRoot();
        var hits = SourceFiles(root)
            .Where(f => f != CompositionRoot)
            .SelectMany(f => HitsIn(CSharpSource.StripComments(File.ReadAllText(Path.Combine(root, f)))).Select(h => $"{f} ({h})"))
            .ToList();

        hits.ShouldBeEmpty(
            "direct clock reads found: inject TimeProvider and call GetUtcNow(); Domain takes 'now' as a parameter (Story 11.17, audit C6). "
            + string.Join("; ", hits));
    }

    [Fact]
    public void The_composition_root_is_the_only_allow_listed_file()
    {
        // Program.cs registers TimeProvider.System once; nothing else may name it.
        var code = CSharpSource.StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), CompositionRoot)));

        code.ShouldContain(SystemClock, Case.Sensitive, $"{CompositionRoot} no longer registers TimeProvider.System: was it moved? Update this guard with it.");
        HitsIn(code).ShouldBe([SystemClock]);
    }

    [Fact]
    public void The_scan_covers_the_source_tree()
    {
        SourceFiles(FindRepoRoot()).Count.ShouldBeGreaterThan(100, "the scan found almost no source files: did the layout move? Update this guard with it.");
    }

    [Fact]
    public void Migrations_bin_and_obj_are_not_scanned()
    {
        IsBuildOrMigrationPath("src/EnergyTracker.Infrastructure.Postgres/Migrations/20260101_X.cs").ShouldBeTrue();
        IsBuildOrMigrationPath("src/EnergyTracker.Api/obj/Debug/X.cs").ShouldBeTrue();
        IsBuildOrMigrationPath("src/EnergyTracker.Api/bin/Debug/X.cs").ShouldBeTrue();
        IsBuildOrMigrationPath("src/EnergyTracker.Application/CreateRoom.cs").ShouldBeFalse();
    }

    // Non-vacuous: the stripper and the matcher are proven against known-bad text, so a refactor of
    // this test cannot silently turn it into one that scans nothing.
    [Fact]
    public void The_scan_itself_detects_what_it_guards_against_and_ignores_comments()
    {
        const string bad = "var a = DateTimeOffset.UtcNow; var b = DateTime.Now; var c = TimeProvider.System; // DateTime.Today\n";
        HitsIn(CSharpSource.StripComments(bad)).ShouldBe(["DateTimeOffset.UtcNow", "DateTime.Now", "TimeProvider.System"]);

        HitsIn(CSharpSource.StripComments("// DateTimeOffset.UtcNow\n/* DateTime.UtcNow, TimeProvider.System */ var x = 1;")).ShouldBeEmpty();
        HitsIn(CSharpSource.StripComments("var now = timeProvider.GetUtcNow();")).ShouldBeEmpty();
        HitsIn(CSharpSource.StripComments("var x = DateTimeOffset.Now; var y = DateTime.UtcNow; var z = DateTime.Today;"))
            .ShouldBe(["DateTimeOffset.Now", "DateTime.UtcNow", "DateTime.Today"]);
    }

    // String and char forms that a naive quote tracker desyncs on must not hide a later clock read.
    [Theory]
    [InlineData("var p = @\"C:\\dir\\\"; var x = DateTime.UtcNow;", "DateTime.UtcNow")]
    [InlineData("var p = $@\"C:\\{d}\\\"; var x = DateTime.UtcNow;", "DateTime.UtcNow")]
    [InlineData("var q = '\"'; var x = DateTime.Now;", "DateTime.Now")]
    [InlineData("var q = '\\''; var s = \"a\"; var x = DateTime.Now;", "DateTime.Now")]
    [InlineData("var s = \"\"\"\n say \"hi\" // not a comment\n \"\"\"; var x = DateTimeOffset.UtcNow;", "DateTimeOffset.UtcNow")]
    [InlineData("var s = $\"\"\"\"\n \"\"\" inside \n \"\"\"\"; var x = DateTime.Today;", "DateTime.Today")]
    [InlineData("var s = \"a // b\"; var x = TimeProvider.System;", "TimeProvider.System")]
    public void The_scan_is_not_fooled_by_verbatim_raw_or_char_literals(string code, string expected)
    {
        HitsIn(CSharpSource.StripComments(code)).ShouldBe([expected]);
    }

    private static List<string> HitsIn(string code) =>
        Forbidden.Where(token => code.Contains(token, StringComparison.Ordinal)).ToList();

    private static List<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildOrMigrationPath(f))
            .ToList();

    private static bool IsBuildOrMigrationPath(string relativePath) =>
        relativePath.Split('/').Any(segment => segment is "Migrations" or "obj" or "bin");

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
