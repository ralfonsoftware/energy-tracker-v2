using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.8 (AC #2, #8; AD-27, audit R2 web side): all HTTP goes through web/src/lib/api-client.ts, which
// owns credentials, the X-Requested-With header and ApiError/toApiError. A call site that talks to fetch
// directly would skip the CSRF header and fail with a 400 that looks like a validation error.
public partial class FrontendUsesTheSharedApiClientTests
{
    private const string ClientFile = "lib/api-client.ts";

    [GeneratedRegex(@"(?<![.\w])fetch\s*\(|\b(?:window|globalThis|self)\.fetch\b")]
    private static partial Regex BareFetch();

    [GeneratedRegex(@"\bclass\s+ApiError\b")]
    private static partial Regex ApiErrorClass();

    [GeneratedRegex(@"\bfunction\s+toApiError\b|\bconst\s+toApiError\b")]
    private static partial Regex ToApiErrorDeclaration();

    [GeneratedRegex(@"credentials\s*:\s*['""]include['""]")]
    private static partial Regex CredentialsInclude();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    // A `//` preceded by `:` is a URL scheme separator inside a string, not a comment.
    [GeneratedRegex(@"(?<!:)//[^\n]*")]
    private static partial Regex LineComment();

    [Fact]
    public void Only_the_shared_client_calls_fetch_or_declares_the_error_types()
    {
        var webSrcDir = FindWebSrcDirectory();
        var offences = new List<string>();

        foreach (var file in ProductionSourceFiles(webSrcDir))
        {
            var relative = Path.GetRelativePath(webSrcDir, file).Replace('\\', '/');
            if (relative == ClientFile)
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(file));
            if (BareFetch().IsMatch(code))
            {
                offences.Add($"{relative}: calls fetch(");
            }
            if (ApiErrorClass().IsMatch(code))
            {
                offences.Add($"{relative}: declares class ApiError");
            }
            if (ToApiErrorDeclaration().IsMatch(code))
            {
                offences.Add($"{relative}: declares toApiError");
            }
            if (CredentialsInclude().IsMatch(code))
            {
                offences.Add($"{relative}: spells credentials: 'include'");
            }
        }

        offences.ShouldBeEmpty(
            "Use apiFetch/ApiError/toApiError from '@/lib/api-client' (story 11.8, AC #2). Found: " + string.Join("; ", offences));
    }

    [Fact]
    public void The_shared_client_exists_and_defines_what_the_scan_forbids_elsewhere()
    {
        // Stops the allowlist emptying silently: renaming the module or its exports fails here.
        var path = Path.Combine(FindWebSrcDirectory(), ClientFile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"{ClientFile} must exist.");

        var code = StripComments(File.ReadAllText(path));
        ApiErrorClass().IsMatch(code).ShouldBeTrue("api-client.ts must define class ApiError.");
        ToApiErrorDeclaration().IsMatch(code).ShouldBeTrue("api-client.ts must define toApiError.");
        BareFetch().IsMatch(code).ShouldBeTrue("api-client.ts must be the one place that calls fetch(.");
        CredentialsInclude().IsMatch(code).ShouldBeTrue("api-client.ts must set credentials: 'include'.");
        code.ShouldContain("X-Requested-With");
    }

    [Fact]
    public void The_scan_finds_the_source_files_it_is_meant_to_check()
    {
        ProductionSourceFiles(FindWebSrcDirectory()).Count().ShouldBeGreaterThan(30);
    }

    private static IEnumerable<string> ProductionSourceFiles(string webSrcDir) =>
        Directory.EnumerateFiles(webSrcDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".test.ts", StringComparison.Ordinal) && !f.EndsWith(".test.tsx", StringComparison.Ordinal));

    private static string StripComments(string source) =>
        LineComment().Replace(BlockComment().Replace(source, string.Empty), string.Empty);

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
