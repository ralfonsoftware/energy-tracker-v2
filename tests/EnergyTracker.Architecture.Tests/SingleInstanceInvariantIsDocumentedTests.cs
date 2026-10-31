using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.13 (AD-24, audit A1): Energy Tracker is correct only with exactly one active instance, and
// only a Bicep default enforced that. This guard keeps the invariant written down and findable:
//  - every process-local mechanism in src is on an explicit list and carries an AD-24 comment, so a NEW
//    one (a ConcurrentDictionary, a Channel, a semaphore, a memory cache, a rate limiter, a temp file)
//    fails here until someone lists it, comments it and adds it to docs/self-hosting.md;
//  - the self-hosting guide names the invariant and every mechanism; both Bicep descriptions and the
//    parameter file point to AD-24, and no maxReplicas value moved off 1;
//  - the heartbeat classes name AD-24.
// Source is scanned as text (comments stripped where the code is meant, kept where the comment is meant),
// like HouseholdImportIsStreamedTests. Each scanned file must still exist and still hold its anchor.
public class SingleInstanceInvariantIsDocumentedTests
{
    private const string FailureHint =
        "AD-24: Energy Tracker supports one active instance. A process-local mechanism needs a one-line AD-24 comment, " +
        "an entry in this guard's list and a row in docs/self-hosting.md ('Run exactly one instance'). Lifting the invariant " +
        "needs the shared-state list in ARCHITECTURE-SPINE/deferred.md ('Multi-instance shared state (lifting AD-24)').";

    // Patterns that mean "state or work lives in this process only".
    private static readonly string[] ProcessLocalPatterns =
    [
        "ConcurrentDictionary<",
        "SemaphoreSlim",
        "Channel.Create",
        "IMemoryCache",
        "AddRateLimiter",
        "Path.GetTempPath",
    ];

    // The files that may contain one of the patterns in code (the seven bound mechanisms of AD-24).
    private static readonly string[] ProcessLocalCodeFiles =
    [
        "src/EnergyTracker.Infrastructure/Adapters/HouseholdRecomputeLock.cs",
        "src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs",
        "src/EnergyTracker.Api/HttpRateLimiting.cs",
        "src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs",
        "src/EnergyTracker.Api/Composition/ApplicationServicesRegistration.cs",
    ];

    // The composition root registers four mechanisms (Story 11.19). Each needs its own AD-24 comment within
    // AnchorWindow lines above (or on) the registration code, so one comment cannot stand in for the others.
    private const string ProgramFile = "src/EnergyTracker.Api/Program.cs";
    private const int AnchorWindow = 6;

    private static readonly (string Mechanism, string File, string Anchor)[] CompositionAnchors =
    [
        ("rate limiter", "src/EnergyTracker.Api/Program.cs", "AddHttpRateLimiting()"),
        ("upload temp path", "src/EnergyTracker.Api/Composition/ApplicationServicesRegistration.cs", "Path.GetTempPath()"),
        ("startup sweep", "src/EnergyTracker.Api/Composition/JobQueueRegistration.cs", "AddSingleton<InProcessJobStartupSweep>()"),
        ("heartbeat", "src/EnergyTracker.Api/Composition/JobQueueRegistration.cs", "AddHostedService<InstanceHeartbeatService>()"),
    ];

    // Files that carry one AD-24 comment at a mechanism: the ones above plus the mechanisms whose code
    // does not match a pattern (the upload registry keeps plain dictionaries behind one lock, the startup
    // sweep is plain code, the restore endpoint writes the temp file through the registry's settings).
    private static readonly string[] AdPointerFiles =
    [
        .. ProcessLocalCodeFiles,
        "src/EnergyTracker.Infrastructure/Adapters/MemoryHouseholdImportUploadRegistry.cs",
        "src/EnergyTracker.Infrastructure/Adapters/InProcessJobStartupSweep.cs",
        "src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs",
        "src/EnergyTracker.Infrastructure/Adapters/InstanceHeartbeatService.cs",
        "src/EnergyTracker.Application/RecordInstanceHeartbeat.cs",
    ];

    // A file that holds several mechanisms needs one AD-24 comment per mechanism, not one for the file
    // (the composition root, counted over its files below). Other files need one.
    private static readonly Dictionary<string, int> MinimumAd24Comments = new()
    {
        ["src/EnergyTracker.Infrastructure/Adapters/InProcessChannelJobQueue.cs"] = 2,
        ["src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs"] = 2,
    };

    // The plain names the self-hosting guide uses for the seven mechanisms (its table).
    private static readonly string[] MechanismNames =
    [
        "Upload registry",
        "Recompute lock",
        "Temp-file payloads",
        "In-process job queue",
        "Startup job sweep",
        "Upload expiry sweep",
        "In-process rate limiter",
    ];

    // Comments that used to claim or imply several replicas work. They must stay gone.
    private static readonly (string File, string Phrase)[] RetiredClaims =
    [
        ("src/EnergyTracker.Api/Endpoints/HouseholdImportEndpoints.cs", "holds across replicas"),
        ("src/EnergyTracker.Infrastructure/Adapters/BackgroundJobProcessor.cs", "scale to zero/multiple replicas"),
        ("src/EnergyTracker.Infrastructure/Adapters/HouseholdRecomputeLock.cs", "hardcodes maxReplicas"),
        ("src/EnergyTracker.Infrastructure/Adapters/MemoryHouseholdImportUploadRegistry.cs", "no evidence of horizontal scale-out"),
    ];

    private const string SelfHostingGuide = "docs/self-hosting.md";
    private const string ContainerAppModule = "infra/modules/container-app.bicep";
    private const string MainBicep = "infra/main.bicep";
    private const string MainBicepParam = "infra/main.bicepparam";

    [Fact]
    public void Every_process_local_mechanism_in_src_is_on_the_allow_list()
    {
        var root = FindRepoRoot();
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !IsBuildOrMigrationPath(f))
            .ToList();
        sourceFiles.Count.ShouldBeGreaterThan(100, "the scan found almost no source files: did the layout move? Update this guard with it.");

        var unlisted = sourceFiles
            .Where(f => !ProcessLocalCodeFiles.Contains(f))
            .Select(f => (File: f, Hits: ProcessLocalHits(StripComments(File.ReadAllText(Path.Combine(root, f))))))
            .Where(x => x.Hits.Count > 0)
            .Select(x => $"{x.File} ({string.Join(", ", x.Hits)})")
            .ToList();

        unlisted.ShouldBeEmpty($"New process-local mechanism(s) not on the AD-24 list: {string.Join("; ", unlisted)}. {FailureHint}");
    }

    [Fact]
    public void Every_allow_listed_mechanism_file_still_exists_and_still_holds_its_pattern()
    {
        foreach (var file in ProcessLocalCodeFiles)
        {
            ProcessLocalHits(CodeOf(file)).ShouldNotBeEmpty($"{file} no longer contains a process-local pattern: was it renamed or fixed? Update this guard and docs/self-hosting.md with it. {FailureHint}");
        }
    }

    [Fact]
    public void Every_mechanism_file_carries_an_AD_24_comment()
    {
        foreach (var file in AdPointerFiles)
        {
            var expected = MinimumAd24Comments.GetValueOrDefault(file, 1);
            CountAd24Comments(CommentsOf(file)).ShouldBeGreaterThanOrEqualTo(expected, $"{file} needs {expected} one-line AD-24 comment(s), one at each process-local mechanism it holds. {FailureHint}");
        }
    }

    [Fact]
    public void Each_composition_root_mechanism_has_its_own_AD_24_comment_next_to_its_registration()
    {
        foreach (var (mechanism, file, anchor) in CompositionAnchors)
        {
            var lines = ReadText(file).Replace("\r\n", "\n").Split('\n');
            var index = Array.FindIndex(lines, l => l.Contains(anchor, StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            index.ShouldBeGreaterThan(-1, $"{file} no longer contains '{anchor}' ({mechanism}): was it moved? Update this guard with it.");
            HasAd24CommentNear(lines, index, AnchorWindow)
                .ShouldBeTrue($"{file}: the {mechanism} registration ('{anchor}') needs an AD-24 comment within {AnchorWindow} lines above it. {FailureHint}");
        }
    }

    private static bool HasAd24CommentNear(string[] lines, int index, int window) =>
        Enumerable.Range(Math.Max(0, index - window), index - Math.Max(0, index - window) + 1)
            .Any(i => CountAd24Comments(lines[i]) > 0);

    [Fact]
    public void No_comment_still_claims_that_several_replicas_work()
    {
        foreach (var (file, phrase) in RetiredClaims)
        {
            ReadText(file).ShouldNotContain(phrase, Case.Insensitive, $"{file} still says '{phrase}', which contradicts AD-24. Point to the invariant instead of arguing it locally. {FailureHint}");
        }
    }

    [Fact]
    public void The_self_hosting_guide_states_the_invariant_names_every_mechanism_and_points_to_AD_24()
    {
        var guide = ReadText(SelfHostingGuide);

        guide.ShouldContain("## Run exactly one instance", Case.Sensitive, $"{SelfHostingGuide} has no 'Run exactly one instance' section. {FailureHint}");
        guide.ShouldContain("AD-24", Case.Sensitive, $"{SelfHostingGuide} does not mention AD-24. {FailureHint}");
        guide.ShouldContain("Multi-instance shared state", Case.Sensitive, $"{SelfHostingGuide} does not point to the shared-state list in deferred.md. {FailureHint}");

        var tableRows = guide.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal)).ToList();
        foreach (var name in MechanismNames)
        {
            tableRows.ShouldContain(row => row.StartsWith("| " + name, StringComparison.Ordinal), $"{SelfHostingGuide} has no table row starting with the '{name}' mechanism. {FailureHint}");
        }
    }

    [Fact]
    public void Both_Bicep_descriptions_and_the_parameter_file_point_to_AD_24_and_every_maxReplicas_value_is_still_one()
    {
        var moduleParam = DescribedParam(ReadText(ContainerAppModule), "maxReplicas");
        moduleParam.Description.ShouldContain("AD-24", Case.Sensitive, $"{ContainerAppModule}: the maxReplicas @description must point to AD-24. {FailureHint}");
        moduleParam.Description.ShouldContain("docs/self-hosting.md", Case.Sensitive, $"{ContainerAppModule}: the maxReplicas @description must point to docs/self-hosting.md.");
        moduleParam.Value.ShouldBe("1", $"{ContainerAppModule}: maxReplicas must stay 1; raising it is an architecture decision (AD-24), not a config change.");

        var mainParam = DescribedParam(ReadText(MainBicep), "containerAppMaxReplicas");
        mainParam.Description.ShouldContain("AD-24", Case.Sensitive, $"{MainBicep}: the containerAppMaxReplicas @description must point to AD-24. {FailureHint}");
        mainParam.Description.ShouldContain("docs/self-hosting.md", Case.Sensitive, $"{MainBicep}: the containerAppMaxReplicas @description must point to docs/self-hosting.md.");
        mainParam.Value.ShouldBe("1", $"{MainBicep}: containerAppMaxReplicas must stay 1 (AD-24).");
        Regex.IsMatch(ReadText(MainBicep), @"^\s*maxReplicas:\s*containerAppMaxReplicas\s*$", RegexOptions.Multiline)
            .ShouldBeTrue($"{MainBicep}: the container app module must receive 'maxReplicas: containerAppMaxReplicas', not a literal that bypasses the guarded parameter. {FailureHint}");

        var paramFile = ReadText(MainBicepParam);
        var lines = paramFile.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\s*param\s+containerAppMaxReplicas\s*=\s*\d+\s*$"));
        index.ShouldBeGreaterThan(0, $"{MainBicepParam} has no 'param containerAppMaxReplicas = <n>' line.");
        Regex.Match(lines[index], @"=\s*(?<value>\d+)").Groups["value"].Value.ShouldBe("1", $"{MainBicepParam}: containerAppMaxReplicas must stay 1 (AD-24).");
        lines[index - 1].ShouldContain("AD-24", Case.Sensitive, $"{MainBicepParam}: the line above containerAppMaxReplicas must be a comment pointing to AD-24.");
        lines[index - 1].TrimStart().ShouldStartWith("//", Case.Sensitive, $"{MainBicepParam}: the AD-24 pointer above containerAppMaxReplicas must be a // comment.");
    }

    // Both queue providers need the heartbeat (an Azure deployment is where a second replica is plausible,
    // a self-host where a second container is), so it is registered at the top level of AddJobQueue, not
    // inside a branch of the queue-provider switch.
    [Fact]
    public void The_heartbeat_is_registered_outside_the_queue_provider_switch()
    {
        const string registrationFile = "src/EnergyTracker.Api/Composition/JobQueueRegistration.cs";
        var code = CodeOf(registrationFile);
        var switchStart = code.IndexOf("switch (jobQueueProvider)", StringComparison.Ordinal);
        var registration = code.IndexOf("AddHostedService<InstanceHeartbeatService>()", StringComparison.Ordinal);
        var methodEnd = code.LastIndexOf("return services;", StringComparison.Ordinal);

        switchStart.ShouldBeGreaterThan(-1, $"{registrationFile} no longer has the queue-provider switch: update this guard with it.");
        registration.ShouldBeGreaterThan(switchStart, $"{registrationFile}: register InstanceHeartbeatService after the queue-provider switch, for both providers (AD-24).");
        methodEnd.ShouldBeGreaterThan(registration);
        BraceDepthAt(code, registration).ShouldBe(BraceDepthAt(code, methodEnd), $"{registrationFile}: InstanceHeartbeatService is registered inside a block (a switch branch?); both queue providers need it (AD-24).");

        var program = CodeOf(ProgramFile);
        var call = program.IndexOf("AddJobQueue(", StringComparison.Ordinal);
        call.ShouldBeGreaterThan(-1, $"{ProgramFile} no longer calls AddJobQueue: the heartbeat would not be registered (AD-24).");
        program.IndexOf("builder.Build()", StringComparison.Ordinal).ShouldBeGreaterThan(call);
    }

    private static int BraceDepthAt(string code, int index) =>
        code[..index].Count(c => c == '{') - code[..index].Count(c => c == '}');

    // Non-vacuous: the matcher and the comment stripper are proven on known text, so a refactor of this
    // test cannot quietly turn it into one that scans nothing.
    [Fact]
    public void The_scan_itself_detects_a_new_mechanism_and_ignores_comments()
    {
        const string source = "private readonly ConcurrentDictionary<Guid, int> _map = new(); // SemaphoreSlim in a comment\nvar p = Path.GetTempPath();";

        ProcessLocalHits(StripComments(source)).ShouldBe(["ConcurrentDictionary<", "Path.GetTempPath"]);
        ProcessLocalHits(StripComments("// IMemoryCache\n/* Channel.Create */ var x = 1;")).ShouldBeEmpty();
        var bicep = DescribedParam("@description('One instance only, see AD-24')\n@minValue(1)\nparam maxReplicas int = 1\n", "maxReplicas");
        bicep.Description.ShouldBe("One instance only, see AD-24");
        bicep.Value.ShouldBe("1");
        string[] near = ["// AD-24: one", "var a = 1;", "var b = 2;", "Register();"];
        HasAd24CommentNear(near, 3, 3).ShouldBeTrue();
        HasAd24CommentNear(near, 3, 2).ShouldBeFalse();
        HasAd24CommentNear(["var s = \"AD-24\";", "Register();"], 1, 3).ShouldBeFalse();
        CountAd24Comments("// AD-24 first\nvar s = \"AD-24 in a string\";\n// no pointer\nvar t = 1; // AD-24 second").ShouldBe(2);
    }

    // One per comment line that names AD-24 (the input is the // comment text of a file).
    private static int CountAd24Comments(string source) =>
        Regex.Matches(source, @"//.*$", RegexOptions.Multiline).Count(m => m.Value.Contains("AD-24", StringComparison.Ordinal));

    private static List<string> ProcessLocalHits(string code) =>
        ProcessLocalPatterns.Where(p => code.Contains(p, StringComparison.Ordinal)).ToList();

    private static bool IsBuildOrMigrationPath(string relativePath) =>
        relativePath.Split('/').Any(segment => segment is "Migrations" or "obj" or "bin");

    // The @description text and the default value of a Bicep param, with any other decorators between them.
    private static (string Description, string Value) DescribedParam(string bicep, string name)
    {
        var match = Regex.Match(
            bicep.Replace("\r\n", "\n"),
            @"@description\('(?<text>(?:\\'|[^'])*)'\)\s*(?:@\w+\([^)]*\)\s*)*param\s+" + Regex.Escape(name) + @"\s+int\s*=\s*(?<value>\d+)");
        match.Success.ShouldBeTrue($"Could not find '@description(...) param {name} int = <n>': was the parameter renamed or reshaped? Update this guard with it.");
        return (match.Groups["text"].Value, match.Groups["value"].Value);
    }

    private static string ReadText(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {relativePath} (looked at {fullPath}).");
        return File.ReadAllText(fullPath);
    }

    private static string CodeOf(string relativePath)
    {
        var code = StripComments(ReadText(relativePath));
        code.Trim().ShouldNotBeEmpty($"{relativePath} has no code after stripping comments.");
        return code;
    }

    // Only the // comment text of a file, so a pointer in a string or an identifier does not count.
    private static string CommentsOf(string relativePath) =>
        string.Join('\n', Regex.Matches(ReadText(relativePath), @"//.*$", RegexOptions.Multiline).Select(m => m.Value));

    // Removes // and /* */ comments but keeps the contents of "regular" string literals (a `//` inside
    // a string is not a comment). None of the scanned files use verbatim or raw string literals.
    private static string StripComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                result.Append(c);
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result.Append(source[i++]);
                    }

                    result.Append(source[i++]);
                }

                if (i < source.Length)
                {
                    result.Append(source[i++]);
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i += 2;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

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
