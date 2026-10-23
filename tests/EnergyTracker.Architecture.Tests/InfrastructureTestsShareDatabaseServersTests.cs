using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Spec ci-pipeline-runtime-speedup: EnergyTracker.Infrastructure.Tests used to start a fresh Postgres or
// SQL Server container for every test method (IAsyncLifetime on the test class), which made up 9 of the 13
// minutes of every deploy and PR run. Classes now use PostgresTestDatabase / SqlServerTestDatabase, a fresh
// database on one shared server per provider (SharedDatabaseServers.cs). A Testcontainers builder anywhere
// else brings the per-test container back. The one exception creates a server-wide login, which must not
// leak into a server other tests use.
public class InfrastructureTestsShareDatabaseServersTests
{
    private const string TestsDirectory = "tests/EnergyTracker.Infrastructure.Tests";

    private static readonly string[] Allowed =
    [
        "SharedDatabaseServers.cs",
        "SmartPlugImportRepositoryAddAsyncMinimalSqlServerPermissionsTests.cs",
    ];

    private static readonly string[] Forbidden = ["new PostgreSqlBuilder(", "new MsSqlBuilder(", "new ContainerBuilder("];

    [Fact]
    public void Infrastructure_tests_build_containers_only_in_the_shared_fixture_and_the_login_test()
    {
        var directory = Path.Combine(FindRepoRoot(), TestsDirectory);
        Directory.Exists(directory).ShouldBeTrue($"Expected to find {directory}");

        var violations = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(file => !Allowed.Contains(Path.GetFileName(file)))
            .SelectMany(file => File.ReadAllLines(file)
                .Select((line, index) => (file, line, number: index + 1))
                .Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                            && Forbidden.Any(f => l.line.Contains(f, StringComparison.Ordinal)))
                .Select(l => $"{Path.GetFileName(l.file)}:{l.number}"))
            .ToList();

        violations.ShouldBeEmpty(
            "Use PostgresTestDatabase / SqlServerTestDatabase (SharedDatabaseServers.cs) instead of a container per " +
            "test class; it gives each test its own database on one shared server.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
    }
}
