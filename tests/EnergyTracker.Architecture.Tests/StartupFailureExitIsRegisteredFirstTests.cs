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

        FirstStatement(bad).ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_rejects_a_program_without_the_registration()
    {
        FirstStatement("using EnergyTracker.Api;\n\nvar builder = WebApplication.CreateBuilder(args);").ShouldNotBe(Registration);
    }

    [Fact]
    public void The_predicate_accepts_the_registration_after_using_directives_and_comments()
    {
        var good = "using EnergyTracker.Api;\n// why\nStartupFailureExit.Register();\n\nvar builder = WebApplication.CreateBuilder(args);";

        FirstStatement(CSharpSource.StripComments(good)).ShouldBe(Registration);
    }

    private static string FirstStatement(string code)
    {
        return code
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("using ", StringComparison.Ordinal))
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
