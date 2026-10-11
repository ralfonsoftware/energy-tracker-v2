using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.5 (CI identity split and supply-chain hygiene, audit S11): every third-party action a
// workflow references must be pinned to a full commit SHA with a trailing `# vX.Y.Z` comment. A
// mutable tag (`actions/checkout@v7`) lets whoever controls the upstream repo change what runs
// with this repo's OIDC token and secrets; the SHA does not move. The comment is what lets
// Dependabot update SHA and version together. Local actions (`./...`) are exempt. Workflows are
// scanned as text — no YAML library, matching the dependency-light sibling guards — and
// whole-line `#` comments are stripped first so explanatory comments can neither satisfy nor break
// the scan (the trailing `# vX.Y.Z` comment is on the `uses:` line itself and is kept).
public class WorkflowActionsArePinnedToCommitShaTests
{
    private const string WorkflowsRelativeDirectory = ".github/workflows";

    // `uses:` as a step key, optionally after the `- ` list marker. The value runs to the first
    // whitespace; whatever follows is the trailing comment.
    private static readonly Regex UsesLine = new(@"^\s*(?:-\s+)?uses:\s*(?<ref>\S+)(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex PinnedReference = new(@"@[0-9a-f]{40}$", RegexOptions.Compiled);

    private static readonly Regex VersionComment = new(@"^\s*#\s*v\d+(\.\d+){0,2}\s*$", RegexOptions.Compiled);

    [Fact]
    public void Every_action_reference_is_pinned_to_a_commit_sha_with_a_version_comment()
    {
        var violations = new List<string>();

        foreach (var (file, lines) in ReadWorkflows())
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var match = UsesLine.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }

                var reference = match.Groups["ref"].Value.Trim('"', '\'');
                if (reference.StartsWith("./", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!PinnedReference.IsMatch(reference))
                {
                    violations.Add($"{file}:{i + 1} '{reference}' is not pinned to a 40-hex commit SHA.");
                }
                else if (!VersionComment.IsMatch(match.Groups["rest"].Value))
                {
                    violations.Add($"{file}:{i + 1} '{reference}' has no trailing '# vX.Y.Z' comment.");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Pin every action to a commit SHA with a version comment (`uses: owner/repo@<40-hex> # v1.2.3`). " +
            "Resolve the SHA with `gh api repos/<owner>/<repo>/commits/<tag> --jq .sha` (commits, not " +
            "git/ref/tags, which can return an annotated tag object). Story 11.5, audit S11:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void Guard_sees_the_workflows_and_their_action_references()
    {
        var references = ReadWorkflows()
            .SelectMany(w => w.Lines)
            .Count(line => UsesLine.IsMatch(line));

        references.ShouldBeGreaterThan(
            0,
            $"No `uses:` lines found under {WorkflowsRelativeDirectory}; the pin guard would pass vacuously.");
    }

    private static List<(string File, string[] Lines)> ReadWorkflows()
    {
        var directory = Path.Combine(FindRepoRoot(), WorkflowsRelativeDirectory);
        Directory.Exists(directory).ShouldBeTrue($"Expected to find {directory}");

        var workflows = Directory.GetFiles(directory, "*.yml")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (
                File: $"{WorkflowsRelativeDirectory}/{Path.GetFileName(path)}",
                Lines: StripWholeLineComments(File.ReadAllText(path)).Split('\n')))
            .ToList();

        workflows.ShouldNotBeEmpty($"No workflow files found in {directory}.");
        return workflows;
    }

    // Blanks whole-line "#" comments (after trimming leading whitespace) but keeps the line count,
    // so reported line numbers match the file.
    private static string StripWholeLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith('#') ? string.Empty : line));

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
