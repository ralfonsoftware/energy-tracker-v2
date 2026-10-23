using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.11 (audit C1; AD-8): the AI backend is configured through exactly one section, Ai:*, and the
// code, appsettings.json, .env.example, docker-compose.yml, scripts/run-api.sh and the Bicep container
// app must all name the same four keys. The audit found the code reading AiPlausibility:* while every
// shipped surface said Ai:*, so a self-hoster who followed .env.example silently got no AI backend. The
// host-level tests in Api.Tests prove the code binds the documented keys; this proves the docs and deploy
// files only name keys the code reads. Each scanned file must still exist and still mention the section,
// so a rename cannot empty the scan.
public class AiConfigurationKeysAreOneSetTests
{
    // The one declaration of the key set. Environment forms are derived: Ai__Endpoint, AI_ENDPOINT.
    private static readonly string[] Keys = ["ApiKey", "BackendLabel", "Endpoint", "Model"];

    // .env names are SNAKE_CASE of the key; Compose and run-api.sh map them to Ai__<Key>.
    private static readonly Dictionary<string, string> EnvNameByKey = new()
    {
        ["Endpoint"] = "AI_ENDPOINT",
        ["ApiKey"] = "AI_API_KEY",
        ["Model"] = "AI_MODEL",
        ["BackendLabel"] = "AI_BACKEND_LABEL",
    };

    private const string Seam = "src/EnergyTracker.Api/AiPlausibilityBackendConfiguration.cs";
    private const string AppSettings = "src/EnergyTracker.Api/appsettings.json";
    private const string EnvExample = ".env.example";
    private const string Compose = "docker-compose.yml";
    private const string RunApi = "scripts/run-api.sh";
    private const string Bicep = "infra/modules/container-app.bicep";

    [Fact]
    public void Code_under_src_reads_the_Ai_section_in_exactly_one_file_and_never_AiPlausibility()
    {
        var files = SourceFiles();
        files.ShouldNotBeEmpty();

        var filesWithAiKeys = new List<string>();
        foreach (var (relative, code) in files)
        {
            code.ShouldNotContain("\"AiPlausibility:", Case.Sensitive,
                $"{relative} reads an AiPlausibility:* key. The configuration section is Ai:* only (Story 11.11, audit C1).");

            var literals = AiKeyLiterals(code);
            if (literals.Count > 0)
            {
                filesWithAiKeys.Add(relative);
                literals.ShouldBe(Keys, ignoreOrder: true,
                    $"{relative} names Ai:* keys {string.Join(", ", literals)}; the set is {string.Join(", ", Keys)}.");
            }
        }

        filesWithAiKeys.ShouldBe([Seam], "The Ai:* keys are read in one place, once, at the composition root (AD-8).");
    }

    [Fact]
    public void The_seam_file_mentions_all_four_keys()
    {
        var code = CodeOf(Seam);

        foreach (var key in Keys)
        {
            code.ShouldContain($"\"Ai:{key}\"", Case.Sensitive, $"{Seam} no longer reads Ai:{key}.");
        }
    }

    [Fact]
    public void appsettings_json_declares_the_Ai_section_with_exactly_the_four_keys()
    {
        var text = TextOf(AppSettings);
        using var document = JsonDocument.Parse(text);

        document.RootElement.TryGetProperty("Ai", out var section).ShouldBeTrue($"{AppSettings} has no \"Ai\" section.");
        section.EnumerateObject().Select(p => p.Name).ShouldBe(Keys, ignoreOrder: true);
        document.RootElement.TryGetProperty("AiPlausibility", out _).ShouldBeFalse($"{AppSettings} still has an AiPlausibility section.");
    }

    [Fact]
    public void env_example_defines_exactly_the_four_AI_variables_and_names_no_other()
    {
        var text = TextOf(EnvExample);
        var defined = Regex.Matches(StripHashComments(text), @"^(AI_[A-Z0-9_]+)=", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

        defined.ShouldBe(EnvNameByKey.Values, ignoreOrder: true, $"{EnvExample} must define exactly the AI_* variables of the key set.");

        // Comments may talk about the variables, but not invent one.
        var mentioned = Regex.Matches(text, @"\bAI_[A-Z][A-Z0-9_]*").Select(m => m.Value).Distinct();
        mentioned.ShouldBeSubsetOf(EnvNameByKey.Values, $"{EnvExample} mentions an AI_* name outside the key set.");
    }

    [Theory]
    [InlineData(Compose)]
    [InlineData(RunApi)]
    public void Compose_and_run_api_map_each_AI_variable_to_its_Ai_key(string file)
    {
        var code = StripHashComments(TextOf(file));

        var pairs = Regex.Matches(code, @"Ai__(\w+)\s*[:=]\s*""\$\{(\w+)")
            .Select(m => (Key: m.Groups[1].Value, Env: m.Groups[2].Value))
            .ToList();

        pairs.Select(p => p.Key).ShouldBe(Keys, ignoreOrder: true, $"{file} must set Ai__<Key> for exactly the key set.");
        foreach (var (key, env) in pairs)
        {
            env.ShouldBe(EnvNameByKey[key], $"{file}: Ai__{key} must come from {EnvNameByKey[key]}.");
        }

        Regex.Matches(code, @"Ai__\w+").Count.ShouldBe(Keys.Length, $"{file} names an Ai__ key more than once or outside the set.");
    }

    [Fact]
    public void The_Bicep_container_app_sets_exactly_the_four_Ai_env_vars()
    {
        var code = StripSlashComments(TextOf(Bicep));

        var names = Regex.Matches(code, @"'Ai__(\w+)'").Select(m => m.Groups[1].Value).ToList();

        names.ShouldBe(Keys, ignoreOrder: true, $"{Bicep} must name exactly the Ai__ env vars of the key set.");
        Regex.Matches(code, @"Ai__\w+").Count.ShouldBe(Keys.Length, $"{Bicep} names an Ai__ key outside the quoted env var names.");
    }

    // Non-vacuous: the extraction helpers are proven against known-bad text, so a refactor of this test
    // cannot silently turn it into one that scans nothing.
    [Fact]
    public void The_scan_itself_finds_stray_keys_and_ignores_comments()
    {
        var code = StripCSharpComments("var a = c[\"Ai:Endpoint\"]; // \"Ai:Hidden\"\nvar b = c[\"Ai:Foo\"]; /* \"Ai:Bar\" */");
        AiKeyLiterals(code).ShouldBe(["Endpoint", "Foo"]);

        StripHashComments("a\n  # Ai__Hidden: x\nAi__Real: y").ShouldNotContain("Hidden");
        StripSlashComments("// 'Ai__Hidden'\nname: 'Ai__Real'").ShouldNotContain("Hidden");
        StripSlashComments("url: 'https://x//y'").ShouldContain("https://x//y");
    }

    // ---- helpers ----

    private static List<(string Relative, string Code)> SourceFiles()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        var separators = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" };
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !separators.Any(path.Contains))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), StripCSharpComments(File.ReadAllText(path))))
            .ToList();
    }

    private static List<string> AiKeyLiterals(string code) =>
        Regex.Matches(code, "\"Ai:(\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();

    private static string CodeOf(string relativePath)
    {
        var code = StripCSharpComments(TextOf(relativePath));
        code.Trim().ShouldNotBeEmpty($"{relativePath} has no code after stripping comments.");
        return code;
    }

    private static string TextOf(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        var text = File.ReadAllText(fullPath);
        Regex.IsMatch(text, "Ai[_:\"]|AI_").ShouldBeTrue($"{relativePath} no longer mentions the Ai section: was it renamed? Update this guard with it.");
        return text;
    }

    // Full-line comments only (.env, yaml, shell): a trailing `#` can be part of a value.
    private static string StripHashComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    // Bicep: full-line // comments only, a // inside a string (a URL) is not a comment.
    private static string StripSlashComments(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // Removes // and /* */ comments but keeps the contents of "regular" string literals.
    private static string StripCSharpComments(string source)
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
