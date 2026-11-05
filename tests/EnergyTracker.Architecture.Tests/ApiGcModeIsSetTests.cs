using System.Xml.Linq;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.25 (AD-13): the API requests Server GC with one heap and a conserve-memory level of 7. At one
// processor (the 0.5 vCPU / 1 GiB container) the runtime falls back to Workstation GC and the conserve level
// does the work (docs/data-import-restore.md, "Memory under a 1 GiB container"). The SDK's own default has
// no conserve level, so deleting these lines changes nothing visible until a large restore runs out of
// memory. A source-text scan of the project file, comments ignored; the rules are a function over the text
// so known-bad text proves they can fail.
public class ApiGcModeIsSetTests
{
    private const string ProjectPath = "src/EnergyTracker.Api/EnergyTracker.Api.csproj";

    private const string KnownGood = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ServerGarbageCollection>true</ServerGarbageCollection>
          </PropertyGroup>
          <ItemGroup>
            <RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="1" />
            <RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="7" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void The_real_Api_project_sets_the_decided_GC_mode()
    {
        Violations(ReadRepoFile(ProjectPath)).ShouldBeEmpty(
            "EnergyTracker.Api.csproj must request Server GC, System.GC.HeapCount 1 and System.GC.ConserveMemory 7 (Story 11.25).");
    }

    [Fact]
    public void The_rules_accept_a_known_good_project_file()
    {
        Violations(KnownGood).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("<ServerGarbageCollection>true</ServerGarbageCollection>", "")]
    [InlineData("<ServerGarbageCollection>true</ServerGarbageCollection>", "<ServerGarbageCollection>false</ServerGarbageCollection>")]
    [InlineData("""<RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="1" />""", "")]
    [InlineData("""<RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="1" />""", """<RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="4" />""")]
    [InlineData("""<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="7" />""", "")]
    [InlineData("""<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="7" />""", """<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="0" />""")]
    public void The_rules_reject_a_project_file_that_drops_or_changes_a_setting(string find, string replace)
    {
        Violations(KnownGood.Replace(find, replace, StringComparison.Ordinal)).ShouldNotBeEmpty();
    }

    [Fact]
    public void The_rules_ignore_a_setting_that_only_appears_in_a_comment()
    {
        var commentedOut = KnownGood.Replace(
            """<RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="1" />""",
            """<!-- <RuntimeHostConfigurationOption Include="System.GC.HeapCount" Value="1" /> -->""",
            StringComparison.Ordinal);

        Violations(commentedOut).ShouldNotBeEmpty();
    }

    private static List<string> Violations(string projectXml)
    {
        var document = XDocument.Parse(projectXml);
        var violations = new List<string>();

        var serverGc = document.Descendants("ServerGarbageCollection").Select(e => e.Value.Trim()).ToList();
        if (!serverGc.Contains("true") || serverGc.Contains("false"))
        {
            violations.Add("ServerGarbageCollection must be set to true.");
        }

        foreach (var (name, value) in new[] { ("System.GC.HeapCount", "1"), ("System.GC.ConserveMemory", "7") })
        {
            var values = document.Descendants("RuntimeHostConfigurationOption")
                .Where(e => (string?)e.Attribute("Include") == name)
                .Select(e => (string?)e.Attribute("Value"))
                .ToList();
            if (values.Count != 1 || values[0] != value)
            {
                violations.Add($"{name} must be set to {value} exactly once.");
            }
        }

        return violations;
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Could not locate repo root (EnergyTracker.sln) from the test base directory.");
        var path = Path.Combine(dir.FullName, relativePath);
        File.Exists(path).ShouldBeTrue($"Expected to find {path}");
        return File.ReadAllText(path);
    }
}
