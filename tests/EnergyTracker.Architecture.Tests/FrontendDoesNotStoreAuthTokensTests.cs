using Shouldly;

namespace EnergyTracker.Architecture.Tests;

public class FrontendDoesNotStoreAuthTokensTests
{
    // Story 8.10 / FR-35: the theme preference (System/Light/Dark) is deliberately stored per device
    // in localStorage. These are the ONLY files allowed to reference it. The stored value is the plain
    // string 'light' | 'dark' (System = nothing stored) — never anything token-like. Compared by path
    // suffix after normalising separators; any new file touching browser storage still fails the guard.
    private static readonly string[] ThemePreferenceAllowlist =
    [
        "lib/color-scheme.ts",
        "lib/color-scheme.test.ts",
    ];

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
            .Where(f => !IsAllowlisted(f))
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

    [Fact]
    public void Theme_preference_allowlist_only_names_files_that_exist()
    {
        // Stops the allowlist silently rotting: renaming color-scheme.ts without updating the guard fails here.
        var webSrcDir = FindWebSrcDirectory();

        foreach (var allowed in ThemePreferenceAllowlist)
        {
            File.Exists(Path.Combine(webSrcDir, allowed.Replace('/', Path.DirectorySeparatorChar)))
                .ShouldBeTrue($"Allowlisted theme-preference file '{allowed}' no longer exists under web/src — update the guard.");
        }
    }

    private static bool IsAllowlisted(string path)
    {
        var normalized = path.Replace('\\', '/');
        return ThemePreferenceAllowlist.Any(allowed => normalized.EndsWith("/" + allowed, StringComparison.Ordinal));
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
