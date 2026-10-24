using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace EnergyTracker.Architecture.Tests;

// Story 11.12 (audit C2; AD-3, AD-17, AD-26): the Household of an /api request is resolved once, in async
// middleware, and the accessor only reads that cached value. Prose rules die, executable ones do not: a
// source-text scan (comments stripped, the convention of HouseholdImportIsStreamedTests) so a later change
// cannot quietly bring back a synchronous lookup in the accessor, a second place that opens the membership
// context, a cross-request cache, a membership claim in the cookie, or a pipeline that registers the
// middleware where it would cost static assets or throttled requests a lookup. Each scanned file must still
// exist and still contain what it is scanned for, so a rename empties nothing.
public partial class HouseholdMembershipIsResolvedOncePerRequestTests
{
    private const string Accessor = "src/EnergyTracker.Infrastructure/Adapters/CurrentHouseholdAccessor.cs";
    private const string Resolver = "src/EnergyTracker.Infrastructure/Adapters/HouseholdMembershipResolver.cs";
    private const string Middleware = "src/EnergyTracker.Api/HouseholdMembershipMiddleware.cs";
    private const string MembershipContext = "src/EnergyTracker.Infrastructure/HouseholdMembershipDbContext.cs";
    private const string ProgramFile = "src/EnergyTracker.Api/Program.cs";

    // The accessor reads a value; it never looks one up, waits for one or remembers one beyond the request.
    private static readonly string[] ForbiddenInAccessor =
    [
        "DbContext",
        "IDbContextFactory",
        "SingleOrDefault",
        "FirstOrDefault",
        "ToList(",
        ".Result",
        ".Wait(",
        "GetAwaiter",
        "IMemoryCache",
        "IDistributedCache",
        "ConcurrentDictionary",
    ];

    // Anything that blocks a thread on a task, in any of the three files that run per request.
    private static readonly string[] BlockingCalls = [".Result", ".Wait(", ".GetAwaiter()", "Task.Run("];

    // The DbSet, not "HouseholdMembership…" (which has HouseholdMembers as a prefix).
    [GeneratedRegex(@"\bHouseholdMembers\b")]
    private static partial Regex HouseholdMembersDbSet();

    [GeneratedRegex(@"\bstatic\b")]
    private static partial Regex StaticKeyword();

    [GeneratedRegex(@"\.(SingleOrDefault|FirstOrDefault|Single|First|ToList|ToArray)\(")]
    private static partial Regex SynchronousQueryMaterialization();

    [GeneratedRegex(@"\bCreateDbContext\(")]
    private static partial Regex SynchronousContextCreation();

    [GeneratedRegex(@"new\s+Claim\(\s*(?<type>[^,)]*)")]
    private static partial Regex NewClaim();

    [GeneratedRegex(@"\bapp\.Map\w*\(")]
    private static partial Regex FirstEndpointMapping();

    [Fact]
    public void The_accessor_only_reads_the_cached_value_and_has_no_lookup_wait_cache_or_static_state()
    {
        var code = CodeOf(Accessor);

        code.ShouldContain("ResolvedHouseholdMembership", Case.Sensitive, $"{Accessor} no longer reads the cached membership: was it renamed? Update this guard with it.");
        code.ShouldContain("TryGet", Case.Sensitive, $"{Accessor} no longer reads the cached membership through TryGet.");
        foreach (var forbidden in ForbiddenInAccessor)
        {
            code.ShouldNotContain(forbidden, Case.Sensitive, $"{Accessor} contains '{forbidden}': the accessor only reads the value the middleware cached for this request (Story 11.12, audit C2, AD-17).");
        }

        HouseholdMembersDbSet().IsMatch(code).ShouldBeFalse($"{Accessor} queries the HouseholdMembers set: the lookup belongs to HouseholdMembershipResolver (Story 11.12).");
        StaticKeyword().IsMatch(code).ShouldBeFalse($"{Accessor} declares static state: membership must not outlive the request (AD-17).");
    }

    // Membership is per request (AD-17): neither the resolver nor the middleware may remember it anywhere that
    // outlives the request, so no cache type and no static state in either.
    [Fact]
    public void The_resolver_and_the_middleware_keep_no_cache_and_no_static_state()
    {
        foreach (var file in new[] { Resolver, Middleware })
        {
            var code = CodeOf(file);
            foreach (var cache in new[] { "IMemoryCache", "MemoryCache", "IDistributedCache", "ConcurrentDictionary", "Dictionary<" })
            {
                code.ShouldNotContain(cache, Case.Sensitive, $"{file} contains '{cache}': membership is never cached across requests (AD-17, AD-26, Story 11.12).");
            }
        }

        StaticKeyword().IsMatch(CodeOf(Resolver)).ShouldBeFalse($"{Resolver} declares static state: membership must not outlive the request (AD-17).");
        var middleware = CodeOf(Middleware);
        middleware.Replace("public static class", string.Empty).Replace("public static IApplicationBuilder", string.Empty)
            .Contains("static", StringComparison.Ordinal).ShouldBeFalse($"{Middleware} declares static state besides its extension method: membership must not outlive the request (AD-17).");
    }

    [Fact]
    public void Nothing_that_runs_per_request_blocks_a_thread_and_the_resolver_is_asynchronous_end_to_end()
    {
        foreach (var file in new[] { Accessor, Resolver, Middleware })
        {
            var code = CodeOf(file);
            foreach (var blocking in BlockingCalls)
            {
                code.ShouldNotContain(blocking, Case.Sensitive, $"{file} contains '{blocking}': a per-request path must not block on a task (Story 11.12, audit C2).");
            }
        }

        var resolver = CodeOf(Resolver);
        resolver.ShouldContain("CreateDbContextAsync", Case.Sensitive, $"{Resolver} no longer creates its context asynchronously.");
        resolver.ShouldContain("SingleOrDefaultAsync", Case.Sensitive, $"{Resolver} no longer queries asynchronously.");
        resolver.ShouldContain("await using", Case.Sensitive, $"{Resolver} must dispose the context it created asynchronously.");
        SynchronousContextCreation().IsMatch(resolver).ShouldBeFalse($"{Resolver} creates a context synchronously.");
        SynchronousQueryMaterialization().IsMatch(resolver).ShouldBeFalse($"{Resolver} materializes a query synchronously.");

        var middleware = CodeOf(Middleware);
        middleware.ShouldContain("StartsWithSegments", Case.Sensitive, $"{Middleware} no longer scopes itself by path.");
        middleware.ShouldContain("IsAuthenticated", Case.Sensitive, $"{Middleware} no longer scopes itself to authenticated requests.");
        middleware.ShouldContain("RequestAborted", Case.Sensitive, $"{Middleware} no longer passes the request's abort token to the lookup.");
    }

    [Fact]
    public void Only_the_resolver_opens_the_membership_context_and_Program_registers_it()
    {
        var allowed = new[] { "HouseholdMembershipDbContext.cs", "HouseholdMembershipResolver.cs", "Program.cs" };

        var users = SourceFiles()
            .Where(f => f.Code.Contains("HouseholdMembershipDbContext", StringComparison.Ordinal))
            .Select(f => f.Name)
            .Order()
            .ToList();

        users.ShouldBe(allowed.Order().ToList(), "Only the context itself, the resolver and the composition root may mention HouseholdMembershipDbContext; a second reader is a second lookup (Story 11.12, AD-3).");
        CodeOf(MembershipContext).ShouldContain("class HouseholdMembershipDbContext", Case.Sensitive);
        SourceFiles()
            .Where(f => f.Name != "HouseholdMembershipResolver.cs" && f.Code.Contains("IDbContextFactory<HouseholdMembershipDbContext>", StringComparison.Ordinal))
            .ShouldBeEmpty("Only the resolver may ask the membership factory for a context.");
    }

    [Fact]
    public void Nothing_about_membership_is_added_to_a_principal_or_the_cookie_ticket()
    {
        var offenders = new List<string>();
        var validatedIssuerClaims = 0;
        foreach (var file in SourceFiles())
        {
            foreach (Match claim in NewClaim().Matches(file.Code))
            {
                if (claim.Groups["type"].Value.Trim() == "HouseholdClaimTypes.ValidatedIssuer")
                {
                    validatedIssuerClaims++;
                }
                else
                {
                    offenders.Add($"{file.RelativePath}: new Claim({claim.Groups["type"].Value.Trim()}, ...)");
                }
            }
        }

        offenders.ShouldBeEmpty("A claim other than the validated issuer was added in src: the Household and member id are resolved per request, never carried in the cookie or the principal (AD-17, Story 11.12).");
        validatedIssuerClaims.ShouldBe(1, "The OIDC OnTokenValidated handler in Program.cs adds exactly one claim, the validated issuer; update this guard if that changed on purpose.");
        CodeOf(ProgramFile).ShouldContain("new Claim(HouseholdClaimTypes.ValidatedIssuer", Case.Sensitive);
    }

    [Fact]
    public void Program_registers_the_membership_middleware_after_the_exception_handler_authentication_authorization_and_rate_limiter_and_before_the_first_endpoint()
    {
        var code = CodeOf(ProgramFile);

        RegistrationOrderProblem(code).ShouldBeNull();
    }

    // Non-vacuous: the order checker and the stripper are proven against known-good and known-bad text, so a
    // refactor of this test cannot silently turn it into one that checks nothing.
    [Fact]
    public void The_scans_themselves_detect_what_they_guard_against_and_ignore_comments()
    {
        const string good =
            "app.UseExceptionHandler();\napp.UseAuthentication();\napp.UseAuthorization();\napp.UseRateLimiter();\napp.UseHouseholdMembership();\napp.MapGet(\"/health\", () => 1);";
        RegistrationOrderProblem(good).ShouldBeNull();

        RegistrationOrderProblem(good.Replace("app.UseHouseholdMembership();\n", string.Empty)).ShouldNotBeNull();
        RegistrationOrderProblem(good.Replace("app.UseRateLimiter();\napp.UseHouseholdMembership();", "app.UseHouseholdMembership();\napp.UseRateLimiter();")).ShouldNotBeNull();
        RegistrationOrderProblem(good.Replace("app.UseAuthorization();\napp.UseRateLimiter();\napp.UseHouseholdMembership();", "app.UseHouseholdMembership();\napp.UseAuthorization();\napp.UseRateLimiter();")).ShouldNotBeNull();
        RegistrationOrderProblem(good.Replace("app.UseExceptionHandler();\napp.UseAuthentication();\napp.UseAuthorization();\napp.UseRateLimiter();\napp.UseHouseholdMembership();", "app.UseAuthentication();\napp.UseAuthorization();\napp.UseRateLimiter();\napp.UseHouseholdMembership();\napp.UseExceptionHandler();")).ShouldNotBeNull();
        RegistrationOrderProblem(good.Replace("app.UseHouseholdMembership();\napp.MapGet(\"/health\", () => 1);", "app.MapGet(\"/health\", () => 1);\napp.UseHouseholdMembership();")).ShouldNotBeNull();
        RegistrationOrderProblem(good + "\napp.UseHouseholdMembership();").ShouldNotBeNull();

        NewClaim().Match("identity.AddClaim(new Claim(\"household_id\", id));").Groups["type"].Value.Trim().ShouldBe("\"household_id\"");
        HouseholdMembersDbSet().IsMatch("dbContext.HouseholdMembers.Where(m => true)").ShouldBeTrue();
        HouseholdMembersDbSet().IsMatch("ResolvedHouseholdMembership.TryGet(http, out var cached)").ShouldBeFalse();
        StaticKeyword().IsMatch("private static readonly Dictionary<string, Guid> Cache = new();").ShouldBeTrue();
        StaticKeyword().IsMatch("private bool _staticElectricity;").ShouldBeFalse();
        SynchronousContextCreation().IsMatch("factory.CreateDbContext()").ShouldBeTrue();
        SynchronousContextCreation().IsMatch("await factory.CreateDbContextAsync(token)").ShouldBeFalse();
        SynchronousQueryMaterialization().IsMatch("query.SingleOrDefault()").ShouldBeTrue();
        SynchronousQueryMaterialization().IsMatch("await query.SingleOrDefaultAsync(token)").ShouldBeFalse();

        var stripped = StripComments("var x = \"//kept\"; // .Result\n/* GetAwaiter */ var y = 1;");
        stripped.ShouldContain("//kept");
        stripped.ShouldNotContain(".Result");
        stripped.ShouldNotContain("GetAwaiter");
    }

    // The first thing wrong with the registration order in `code`, or null when it is right.
    private static string? RegistrationOrderProblem(string code)
    {
        if (CountOf(code, "UseHouseholdMembership(") != 1)
        {
            return "app.UseHouseholdMembership() must be registered exactly once.";
        }

        var membership = code.IndexOf("app.UseHouseholdMembership();", StringComparison.Ordinal);
        if (membership < 0)
        {
            return "app.UseHouseholdMembership(); is not registered as a statement of its own.";
        }

        foreach (var before in new[] { "app.UseExceptionHandler();", "app.UseAuthentication();", "app.UseAuthorization();", "app.UseRateLimiter();" })
        {
            var index = code.IndexOf(before, StringComparison.Ordinal);
            if (index < 0)
            {
                return $"'{before}' was not found: was it renamed? Update this guard with it.";
            }

            if (index > membership)
            {
                return $"app.UseHouseholdMembership() must come after {before} (a 401, a 429 and a database failure must not pay for or hide behind the lookup).";
            }
        }

        var firstEndpoint = FirstEndpointMapping().Match(code);
        if (!firstEndpoint.Success)
        {
            return "No app.Map…( call found: was the endpoint mapping renamed? Update this guard with it.";
        }

        return firstEndpoint.Index < membership
            ? $"app.UseHouseholdMembership() must come before the first endpoint mapping ({firstEndpoint.Value})."
            : null;
    }

    private sealed record SourceFile(string Name, string RelativePath, string Code);

    private static List<SourceFile> SourceFiles()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var segments = Path.GetRelativePath(src, path).Split(Path.DirectorySeparatorChar);
                return !segments.Contains("obj") && !segments.Contains("bin")
                    && !segments.Any(s => s.StartsWith("EnergyTracker.Infrastructure.Migrations", StringComparison.Ordinal));
            })
            .Select(path => new SourceFile(Path.GetFileName(path), Path.GetRelativePath(root, path), StripComments(File.ReadAllText(path))))
            .ToList();
    }

    private static string CodeOf(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected to find {fullPath}");
        var code = StripComments(File.ReadAllText(fullPath));
        code.Trim().ShouldNotBeEmpty($"{relativePath} has no code after stripping comments.");
        return code;
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // Removes // and /* */ comments but keeps the contents of "regular" string literals (a `//` inside
    // a string, such as a URL, is not a comment).
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

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (EnergyTracker.sln) from test base directory.");
    }
}
