using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.6 (AC #2, #8; AD-17): the Key Vault Data Protection protector needs
// Azure.Extensions.AspNetCore.DataProtection.Keys, and Azure.Identity must be pinned explicitly
// rather than left to whatever Microsoft.Data.SqlClient happens to pull in transitively. Central
// package management only pins a version; the explicit <PackageReference> in the Api project (without
// a Version=) is what actually makes the pin bite.
public class DataProtectionKeyVaultPackagesTests
{
    private const string PackagesProps = "Directory.Packages.props";
    private const string ApiProject = "src/EnergyTracker.Api/EnergyTracker.Api.csproj";

    [Theory]
    [InlineData("Azure.Extensions.AspNetCore.DataProtection.Keys")]
    [InlineData("Azure.Identity")]
    public void Package_is_pinned_centrally_and_referenced_by_the_api_project_without_a_version(string package)
    {
        var props = Read(PackagesProps);
        Regex.IsMatch(props, $@"<PackageVersion\s+Include=""{Regex.Escape(package)}""\s+Version=""[^""]+""")
            .ShouldBeTrue($"{PackagesProps}: add `<PackageVersion Include=\"{package}\" Version=\"...\" />` (AD-17).");

        var csproj = Read(ApiProject);
        Regex.IsMatch(csproj, $@"<PackageReference\s+Include=""{Regex.Escape(package)}""\s*/>")
            .ShouldBeTrue(
                $"{ApiProject}: add `<PackageReference Include=\"{package}\" />` without Version= (central package management). " +
                "A central pin that no project references does nothing.");
    }

    private static string Read(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {relativePath} (looked at {fullPath}).");
        return File.ReadAllText(fullPath);
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
