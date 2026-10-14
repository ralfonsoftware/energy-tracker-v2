using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.6 (AC #1, #8; AD-17 amendment 2026-10-10, audit S2): the Data Protection key ring is
// wrapped by one Azure Key Vault key, reached through the Container App's system-assigned identity.
// The shape that has to stay true is: an RBAC-model vault (no access policies), exactly one key, the
// "Key Vault Crypto Service Encryption User" role assigned on that KEY (never on the vault), and the
// key id handed to the app as DataProtection__KeyVaultKeyId. Bicep is scanned as text — no Bicep
// library, matching the sibling workflow guards — with whole-line `//` comments stripped first so an
// explanatory comment can neither satisfy nor break an assertion.
public class DataProtectionKeyVaultInfraTests
{
    private const string KeyVaultModule = "infra/modules/key-vault.bicep";
    private const string ContainerAppModule = "infra/modules/container-app.bicep";
    private const string MainBicep = "infra/main.bicep";

    // Built-in role "Key Vault Crypto Service Encryption User": keys/read, wrap, unwrap — nothing else.
    private const string CryptoServiceEncryptionUserRoleId = "e147488a-f6f5-4113-8e2d-b22465e65bf6";

    private static readonly Regex ResourceBlock = new(
        @"^resource\s+(?<symbol>\w+)\s+'(?<type>[^']+)'(?<existing>\s+existing)?\s*=\s*(?:if\s*\([^)]*\)\s*)?\{(?<body>.*?)^\}",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    [Fact]
    public void Key_vault_module_uses_the_rbac_permission_model_and_no_access_policies()
    {
        var bicep = ReadBicep(KeyVaultModule);

        Regex.IsMatch(bicep, @"enableRbacAuthorization\s*:\s*true")
            .ShouldBeTrue($"{KeyVaultModule}: set `enableRbacAuthorization: true` on the vault (AD-17: RBAC permission model).");

        bicep.ShouldNotContain(
            "accessPolicies",
            Case.Insensitive,
            $"{KeyVaultModule}: remove `accessPolicies`. Access is granted only by the role assignment on the key (AD-17).");
    }

    [Fact]
    public void Key_vault_module_declares_exactly_one_key()
    {
        var bicep = ReadBicep(KeyVaultModule);

        var keys = ResourceBlock.Matches(bicep)
            .Where(m => m.Groups["type"].Value.StartsWith("Microsoft.KeyVault/vaults/keys@", StringComparison.Ordinal))
            .ToList();

        keys.Count.ShouldBe(
            1,
            $"{KeyVaultModule} must declare exactly one Microsoft.KeyVault/vaults/keys resource (the Data Protection wrapping key); found {keys.Count}.");

        var body = keys[0].Groups["body"].Value;
        body.ShouldNotContain(
            "exp:",
            Case.Sensitive,
            $"{KeyVaultModule}: the key must not carry an expiry (`exp`); an expired key would make the key ring undecryptable.");
        body.ShouldNotContain(
            "rotationPolicy",
            Case.Insensitive,
            $"{KeyVaultModule}: the key must not carry a rotation policy; rotation is not part of this design (Story 11.6).");
    }

    [Fact]
    public void Crypto_service_encryption_user_role_is_assigned_on_the_key_resource_not_the_vault()
    {
        var bicep = ReadBicep(ContainerAppModule);
        var blocks = ResourceBlock.Matches(bicep).ToList();

        var roleVariables = Regex.Matches(bicep, @"^var\s+(?<name>\w+)\s*=.*" + Regex.Escape(CryptoServiceEncryptionUserRoleId), RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        var assignment = blocks.SingleOrDefault(m =>
            m.Groups["type"].Value.StartsWith("Microsoft.Authorization/roleAssignments@", StringComparison.Ordinal)
            && !m.Groups["existing"].Success
            && (m.Groups["body"].Value.Contains(CryptoServiceEncryptionUserRoleId, StringComparison.OrdinalIgnoreCase)
                || roleVariables.Any(v => Regex.IsMatch(m.Groups["body"].Value, $@"\b{Regex.Escape(v)}\b"))));

        assignment.ShouldNotBeNull(
            $"{ContainerAppModule} needs exactly one role assignment for role {CryptoServiceEncryptionUserRoleId} " +
            "(Key Vault Crypto Service Encryption User) for the Container App's identity. Model it on acrPullRoleAssignment.");

        var scope = Regex.Match(assignment.Groups["body"].Value, @"^\s*scope\s*:\s*(?<symbol>\w+)\s*$", RegexOptions.Multiline);
        scope.Success.ShouldBeTrue(
            $"{ContainerAppModule}: the Key Vault role assignment must set `scope:` to the key resource symbol.");

        var scopeSymbol = scope.Groups["symbol"].Value;
        var scopeResource = blocks.SingleOrDefault(m => m.Groups["symbol"].Value == scopeSymbol);
        scopeResource.ShouldNotBeNull(
            $"{ContainerAppModule}: scope `{scopeSymbol}` is not a resource declared in this module.");

        scopeResource.Groups["type"].Value.ShouldStartWith(
            "Microsoft.KeyVault/vaults/keys@",
            Case.Sensitive,
            $"{ContainerAppModule}: the role must be scoped to the key (Microsoft.KeyVault/vaults/keys), not `{scopeSymbol}` " +
            "(AD-17: \"on that key only\"). A vault-scoped assignment would let the identity use every key in the vault.");
    }

    [Fact]
    public void Container_app_gets_the_key_id_as_the_data_protection_env_var()
    {
        var bicep = ReadBicep(ContainerAppModule);

        Regex.IsMatch(bicep, @"name\s*:\s*'DataProtection__KeyVaultKeyId'")
            .ShouldBeTrue($"{ContainerAppModule}: add the env var `DataProtection__KeyVaultKeyId` (the versionless key URI) to the container.");
    }

    [Fact]
    public void Main_bicep_instantiates_the_key_vault_module_and_passes_the_key_to_the_container_app()
    {
        var bicep = ReadBicep(MainBicep);

        Regex.IsMatch(bicep, @"module\s+\w+\s+'modules/key-vault\.bicep'")
            .ShouldBeTrue($"{MainBicep}: instantiate `modules/key-vault.bicep` and pass its key outputs to the container app.");

        Regex.IsMatch(bicep, @"dataProtectionKeyUri\s*:")
            .ShouldBeTrue($"{MainBicep}: pass `dataProtectionKeyUri` (and the vault/key names) from the key vault module to the container app.");
    }

    private static string ReadBicep(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {relativePath} (looked at {fullPath}).");

        var withoutComments = Regex.Replace(File.ReadAllText(fullPath), @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return withoutComments.Replace("\r\n", "\n");
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
