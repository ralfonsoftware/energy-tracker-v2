using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.23 (AD-19, ADR 0009): the process-level exit hangs on the process, not on a list of throw sites, so a
// new throw needs no extra step. That only holds while the handler is subscribed before anything can throw:
// the first statement of Program.cs, ahead of WebApplication.CreateBuilder. The predicate is proven against
// known-bad text so a refactor cannot turn the guard into one that passes on anything.
public class StartupFailureExitIsRegisteredFirstTests
{
    private const string ProgramFile = "src/EnergyTracker.Api/Program.cs";
    private const string Registration = "StartupFailureExit.Register();";
    private const string CreateBuilder = "WebApplication.CreateBuilder";

    [Fact]
    public void Program_registers_the_startup_failure_exit_as_its_first_statement_before_the_builder()
    {
        var code = ProgramCode();

        FirstStatement(code).ShouldBe(Registration, "Program.cs must open with the StartupFailureExit registration, before anything that can throw (Story 11.23, ADR 0009).");
        code.IndexOf(Registration, StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf(CreateBuilder, StringComparison.Ordinal));
    }

    [Fact]
    public void The_predicate_rejects_a_registration_that_comes_after_the_builder()
    {
        var bad = """
            using EnergyTracker.Api;

            var builder = WebApplication.CreateBuilder(args);
            StartupFailureExit.Register();
            """;

        FirstStatement(CSharpSource.StripComments(bad)).ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_rejects_a_program_without_the_registration()
    {
        FirstStatement(CSharpSource.StripComments("using EnergyTracker.Api;\n\nvar builder = WebApplication.CreateBuilder(args);")).ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_rejects_a_using_statement_before_the_registration()
    {
        // A using declaration runs code, so it is a statement, not a directive.
        var bad = """
            using EnergyTracker.Api;

            using var scope = Something.Throwing();
            StartupFailureExit.Register();
            """;

        FirstStatement(CSharpSource.StripComments(bad)).ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_rejects_a_statement_sharing_the_line_before_the_registration()
    {
        FirstStatement(CSharpSource.StripComments("using EnergyTracker.Api;\nvar x = Throwing(); StartupFailureExit.Register();")).ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_skips_every_kind_of_using_directive()
    {
        var good = """
            global using System;
            using EnergyTracker.Api;
            using static System.Math;
            using Alias = System.Text.StringBuilder;
            StartupFailureExit.Register();
            """;

        FirstStatement(CSharpSource.StripComments(good)).ShouldBe(Registration);
    }

    [Fact]
    public void The_predicate_accepts_the_registration_after_using_directives_and_comments()
    {
        var good = "using EnergyTracker.Api;\n// why\nStartupFailureExit.Register();\n\nvar builder = WebApplication.CreateBuilder(args);";

        FirstStatement(CSharpSource.StripComments(good)).ShouldBe(Registration);
    }

    // A using directive names a namespace or type and ends right there; a using declaration or statement
    // (`using var x = ...;`, `using (...)`) runs code and therefore counts as the first statement.
    private static readonly Regex UsingDirective = new(
        @"^(global\s+)?using\s+(static\s+)?([A-Za-z_][\w]*\s*=\s*)?[A-Za-z_][\w.<>, ]*;$",
        RegexOptions.CultureInvariant);

    private static string FirstStatement(string code)
    {
        return code
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !UsingDirective.IsMatch(line))
            ?? string.Empty;
    }

    private static string ProgramCode()
    {
        var fullPath = Path.Combine(FindRepoRoot(), ProgramFile);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        var code = CSharpSource.StripComments(File.ReadAllText(fullPath));
        code.ShouldContain(CreateBuilder, Case.Sensitive, $"{ProgramFile} no longer calls {CreateBuilder}: update this guard with it.");
        return code;
    }

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
