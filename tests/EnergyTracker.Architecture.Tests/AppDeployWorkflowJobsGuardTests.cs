using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Spec ci-pipeline-runtime-speedup: app-deploy.yml runs frontend-tests, backend-tests and image-build in
// parallel and a `deploy` job that needs all three. The split only keeps its promise (nothing is migrated,
// pushed or rolled out unless every test is green) while the jobs stay as drawn: the deploy job gates on all
// three, and only it can reach Azure or the registry. Workflows are scanned as text, with whole-line `#`
// comments stripped, like the sibling workflow guards.
public class AppDeployWorkflowJobsGuardTests
{
    private const string RelativeFilePath = ".github/workflows/app-deploy.yml";
    private const string DeployJob = "deploy";

    private static readonly string[] GateJobs = ["frontend-tests", "backend-tests", "image-build"];

    // Job keys sit at two-space indent directly under `jobs:`.
    private static readonly Regex JobStart = new(@"^  (?<id>[A-Za-z0-9_-]+):\s*$", RegexOptions.Compiled);

    [Fact]
    public void Deploy_needs_the_frontend_tests_the_backend_tests_and_the_image_build()
    {
        var jobs = ReadJobs();
        jobs.Keys.ShouldBe([.. GateJobs, DeployJob], ignoreOrder: true, "app-deploy.yml jobs changed; update this guard with them.");

        var needs = Regex.Match(jobs[DeployJob], @"^    needs:\s*\[(?<ids>[^\]]*)\]", RegexOptions.Multiline);
        needs.Success.ShouldBeTrue("Job 'deploy' must declare `needs: [...]` on one line.");

        var declared = needs.Groups["ids"].Value.Split(',').Select(id => id.Trim()).ToArray();
        declared.ShouldBe(GateJobs, ignoreOrder: true,
            "Job 'deploy' must need every gate: a missing one lets a red test or a failed image build still deploy.");
    }

    [Fact]
    public void Only_the_deploy_job_can_reach_azure_the_registry_or_secrets()
    {
        var jobs = ReadJobs();
        string[] forbidden = ["azure/login", "id-token", "push: true", "secrets.", "az "];

        foreach (var gate in GateJobs)
        {
            foreach (var token in forbidden)
            {
                jobs[gate].ShouldNotContain(
                    token,
                    Case.Sensitive,
                    $"Job '{gate}' runs before the tests are known to be green and must not use '{token}'; " +
                    "that belongs in the deploy job only.");
            }
        }

        jobs[DeployJob].ShouldContain("id-token: write", Case.Sensitive, "The deploy job logs in with OIDC and needs `id-token: write`.");
        jobs[DeployJob].ShouldContain("push: true", Case.Sensitive, "The image is pushed in the deploy job.");
    }

    [Fact]
    public void The_workflow_level_permissions_do_not_grant_id_token()
    {
        var text = StripWholeLineComments(File.ReadAllText(Path.Combine(FindRepoRoot(), RelativeFilePath)));
        var beforeJobs = text[..text.IndexOf("\njobs:", StringComparison.Ordinal)];

        beforeJobs.ShouldNotContain("id-token", Case.Sensitive, "Grant `id-token: write` on the deploy job only.");
    }

    private static Dictionary<string, string> ReadJobs()
    {
        var fullPath = Path.Combine(FindRepoRoot(), RelativeFilePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");

        var lines = StripWholeLineComments(File.ReadAllText(fullPath)).Split('\n');
        var jobsIndex = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        jobsIndex.ShouldBeGreaterThanOrEqualTo(0, $"No top-level `jobs:` in {RelativeFilePath}.");

        var jobs = new Dictionary<string, string>();
        string? current = null;
        var body = new List<string>();

        for (var i = jobsIndex + 1; i < lines.Length; i++)
        {
            var match = JobStart.Match(lines[i]);
            if (match.Success)
            {
                if (current is not null)
                {
                    jobs[current] = string.Join('\n', body);
                }

                current = match.Groups["id"].Value;
                body.Clear();
            }
            else if (current is not null)
            {
                body.Add(lines[i]);
            }
        }

        if (current is not null)
        {
            jobs[current] = string.Join('\n', body);
        }

        return jobs;
    }

    private static string StripWholeLineComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line => line.TrimStart().StartsWith('#') ? string.Empty : line));

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
