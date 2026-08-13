using Shouldly;

namespace EnergyTracker.Architecture.Tests;

public class FrontendDoesNotStoreAuthTokensTests
{
    [Fact]
    public void Frontend_source_never_reads_or_writes_localStorage_or_sessionStorage()
    {
        // AC #3: identity lives in a server-side httpOnly cookie only — the SPA must never be
        // able to read an equivalent token via JS. localStorage/sessionStorage are the two
        // JS-readable browser storage mechanisms an accidental token-caching bug would use.
        var webSrcDir = FindWebSrcDirectory();

        var offendingFiles = Directory
            .EnumerateFiles(webSrcDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f =>
            {
                var content = File.ReadAllText(f);
                return content.Contains("localStorage", StringComparison.Ordinal)
                    || content.Contains("sessionStorage", StringComparison.Ordinal);
            })
            .ToList();

        offendingFiles.ShouldBeEmpty(
            $"No frontend source file should reference localStorage/sessionStorage (AC #3). Found: {string.Join(", ", offendingFiles)}");
    }

    private static string FindWebSrcDirectory()
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

        var webSrcDir = Path.Combine(dir.FullName, "web", "src");
        Directory.Exists(webSrcDir).ShouldBeTrue($"Expected to find {webSrcDir}");
        return webSrcDir;
    }
}
