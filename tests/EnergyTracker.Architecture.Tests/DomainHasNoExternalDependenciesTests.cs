using System.Xml.Linq;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

public class DomainHasNoExternalDependenciesTests
{
    [Fact]
    public void Domain_csproj_has_no_PackageReference_beyond_the_BCL()
    {
        var csprojPath = FindDomainCsproj();

        var project = XDocument.Load(csprojPath);
        var packageReferences = project.Descendants("PackageReference")
            .Select(pr => pr.Attribute("Include")?.Value)
            .ToList();

        packageReferences.ShouldBeEmpty(
            $"EnergyTracker.Domain must have zero external package references beyond the BCL (AD-1). Found: {string.Join(", ", packageReferences)}");
    }

    private static string FindDomainCsproj()
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

        var csprojPath = Path.Combine(dir.FullName, "src", "EnergyTracker.Domain", "EnergyTracker.Domain.csproj");
        File.Exists(csprojPath).ShouldBeTrue($"Expected to find {csprojPath}");
        return csprojPath;
    }
}
