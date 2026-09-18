using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// AD-9 / UtcDateTimeOffsetConverter coupling (spec-datetimeoffset-utc-normalization.md): the
// EnergyTrackerDbContext-wide UTC-normalizing convention (ToUniversalTime() on every
// DateTimeOffset write) is safe for EveHomeXlsxParser's parsed timestamps only because that
// parser deliberately constructs them with TimeSpan.Zero (local wall-clock time, never
// UTC-converted). If this file is ever changed to emit a real local offset instead, the
// convention would then shift those wall-clock values across midnight boundaries — the exact
// corruption AD-9 exists to prevent. This guard pins the pairing so that change cannot happen
// silently.
//
// Hardened per review loop 1 (Edge Case Hunter): enumerates EVERY "new DateTimeOffset(" call in
// the file and asserts each one uses TimeSpan.Zero — a single IsMatch-for-one-zero-offset-call
// regex would still pass if a second, non-zero-offset construction were added elsewhere in the
// same file. Uses a balanced-paren scan rather than a bounded-nesting-depth regex, so a compliant
// refactor with more than one level of nested parens in its argument (e.g. a nested method call
// inside another nested call) doesn't false-fail the build.
//
// Hardened again per review loop 2 (Edge Case Hunter / Blind Hunter): (a) a plain substring
// Contains("TimeSpan.Zero") on the whole argument-list text would be fooled by a *nested* "new
// DateTimeOffset(x, TimeSpan.Zero)" call inside an outer, non-zero-offset call's arguments — the
// outer call's own top-level arguments are now split (respecting nesting) and checked
// individually; (b) the scan now resumes searching from just after each call's opening paren, not
// past its entire argument list, so a "new DateTimeOffset(" nested inside another one's arguments
// is still found as its own call; (c) whole-line `//` comments are stripped before scanning —
// matching this repo's own established precedent for this exact kind of guard
// (PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests) — so a comment merely mentioning
// "new DateTimeOffset(" with example/unbalanced parens can't be misread as real code.
public class EveHomeXlsxParserUsesZeroOffsetForUtcNormalizationConventionTests
{
    private const string RelativeFilePath = "src/EnergyTracker.Infrastructure/Adapters/EveHomeXlsxParser.cs";
    private const string ConstructorCallStart = "new DateTimeOffset(";

    [Fact]
    public void Every_DateTimeOffset_constructed_by_EveHomeXlsxParser_uses_TimeSpan_Zero()
    {
        var repoRoot = FindRepoRoot();
        var fullPath = Path.Combine(repoRoot, RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        var source = StripWholeLineComments(File.ReadAllText(fullPath));
        var callArgumentLists = FindDateTimeOffsetConstructorArguments(source);

        callArgumentLists.ShouldNotBeEmpty(
            $"Expected at least one '{ConstructorCallStart}' call in {RelativeFilePath} — " +
            "if this parser no longer constructs a DateTimeOffset directly, this guard (and its " +
            "reason for existing) needs to move to wherever it now does.");

        foreach (var arguments in callArgumentLists)
        {
            var topLevelArguments = SplitTopLevelArguments(arguments);
            topLevelArguments.Any(a => a.Trim() == "TimeSpan.Zero").ShouldBeTrue(
                "EveHomeXlsxParser must construct every DateTimeOffset with TimeSpan.Zero as a direct, " +
                "top-level argument. The DbContext-wide UTC-normalizing convention (UtcDateTimeOffsetConverter) " +
                "relies on this offset already being zero to be a no-op for Eve Home data (AD-9) — a real local " +
                "offset here would make the convention shift wall-clock values across midnight boundaries. " +
                $"Offending call arguments: {arguments}");
        }
    }

    // Finds each "new DateTimeOffset(" occurrence and walks forward counting parens to locate the
    // matching close paren, returning the raw argument-list text between them. Handles arbitrarily
    // deep nesting (e.g. a DateTime.Parse(...) call as an argument), unlike a fixed-depth regex.
    // Resumes scanning from just after the opening paren (not past the whole call) so a "new
    // DateTimeOffset(" nested inside another one's arguments is still found as its own call.
    private static List<string> FindDateTimeOffsetConstructorArguments(string source)
    {
        var results = new List<string>();
        var searchStart = 0;

        while (true)
        {
            var callStart = source.IndexOf(ConstructorCallStart, searchStart, StringComparison.Ordinal);
            if (callStart < 0)
            {
                break;
            }

            var argsStart = callStart + ConstructorCallStart.Length;
            var depth = 1;
            var i = argsStart;
            while (i < source.Length && depth > 0)
            {
                if (source[i] == '(')
                {
                    depth++;
                }
                else if (source[i] == ')')
                {
                    depth--;
                }

                i++;
            }

            if (depth != 0)
            {
                throw new InvalidOperationException(
                    $"Unbalanced parens scanning for '{ConstructorCallStart}' in {RelativeFilePath} starting at offset {callStart}.");
            }

            results.Add(source[argsStart..(i - 1)]);
            searchStart = argsStart;
        }

        return results;
    }

    // Splits an argument-list's top-level comma-separated arguments, respecting nested parens so a
    // comma inside a nested call (e.g. "DateTime.Parse(a, b, c)") doesn't get treated as a top-level
    // argument separator. This is what stops a nested "new DateTimeOffset(x, TimeSpan.Zero)" call's
    // own TimeSpan.Zero from being mistaken for the *outer* call's own top-level offset argument.
    private static List<string> SplitTopLevelArguments(string argumentListText)
    {
        var arguments = new List<string>();
        var depth = 0;
        var currentStart = 0;

        for (var i = 0; i < argumentListText.Length; i++)
        {
            switch (argumentListText[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(argumentListText[currentStart..i]);
                    currentStart = i + 1;
                    break;
            }
        }

        arguments.Add(argumentListText[currentStart..]);
        return arguments;
    }

    // Matches PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests' own precedent: drops any
    // line that is a whole-line "//" comment (after trimming leading whitespace) before scanning,
    // so a comment mentioning "new DateTimeOffset(" with illustrative/unbalanced parens can't be
    // misread as real code by the balanced-paren scan below.
    private static string StripWholeLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

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
