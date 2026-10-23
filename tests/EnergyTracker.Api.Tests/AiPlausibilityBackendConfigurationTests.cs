using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.11 (AC #4, #9; audit C1; AD-8): the AI backend is configured through the Ai:* section that
// appsettings.json, .env.example, docker-compose.yml and Bicep document, read once at the composition root
// by AiPlausibilityBackendConfiguration. The audit found the code reading AiPlausibility:* instead, so a
// self-hoster who followed the docs silently got the no-op client. The seam takes an IServiceCollection
// and an IConfiguration, so most of this needs no host; the last tests prove the real host binds it.
public class AiPlausibilityBackendConfigurationTests
{
    private const string Endpoint = "http://localhost:1234";

    private static (ServiceProvider Provider, AiPlausibilityBackendConfigurationResult Result) BuildFrom(
        IConfiguration configuration)
    {
        var services = new ServiceCollection().AddLogging();
        var result = AiPlausibilityBackendConfiguration.Configure(services, configuration);
        return (services.BuildServiceProvider(), result);
    }

    private static IConfiguration ConfigurationOf(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static (ServiceProvider Provider, AiPlausibilityBackendConfigurationResult Result) Build(Dictionary<string, string?> values) =>
        BuildFrom(ConfigurationOf(values));

    [Fact]
    public void All_four_documented_keys_select_the_OpenAI_compatible_client_with_the_key_as_a_bearer_token()
    {
        var (provider, result) = Build(new()
        {
            ["Ai:Endpoint"] = "https://api.example.test:8443",
            ["Ai:ApiKey"] = "k-secret-value",
            ["Ai:Model"] = "gpt-test",
            ["Ai:BackendLabel"] = "Example cloud",
        });
        using var _ = provider;

        provider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<OpenAiCompatibleClient>();
        provider.GetRequiredService<AiPlausibilityBackendOptions>().ShouldBe(
            new AiPlausibilityBackendOptions(Configured: true, Label: "Example cloud", Model: "gpt-test"));
        result.Configured.ShouldBeTrue();

        // The typed client's name is its service type's name: that is how its configuration is read back.
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAiPlausibilityClient));
        client.BaseAddress.ShouldBe(new Uri("https://api.example.test:8443"));
        client.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Bearer");
        client.DefaultRequestHeaders.Authorization.Parameter.ShouldBe("k-secret-value");
    }

    [Fact]
    public void Only_the_endpoint_set_means_the_default_model_no_label_and_no_authorization_header()
    {
        var (provider, _) = Build(new() { ["Ai:Endpoint"] = Endpoint });
        using var _ = provider;

        provider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<OpenAiCompatibleClient>();
        provider.GetRequiredService<AiPlausibilityBackendOptions>().ShouldBe(
            new AiPlausibilityBackendOptions(Configured: true, Label: null, Model: "default"));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAiPlausibilityClient));
        client.DefaultRequestHeaders.Authorization.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_blank_or_whitespace_endpoint_selects_the_no_op_client(string? endpoint)
    {
        var (provider, result) = Build(new()
        {
            ["Ai:Endpoint"] = endpoint,
            ["Ai:ApiKey"] = "ignored-without-an-endpoint",
            ["Ai:Model"] = "  ",
            ["Ai:BackendLabel"] = "  ",
        });
        using var _ = provider;

        provider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<NoOpAiPlausibilityClient>();
        provider.GetRequiredService<AiPlausibilityBackendOptions>().ShouldBe(
            new AiPlausibilityBackendOptions(Configured: false, Label: null, Model: "default"));
        result.Configured.ShouldBeFalse();
        result.Description.ShouldContain("none configured");
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var (provider, _) = Build(new()
        {
            ["Ai:Endpoint"] = $"  {Endpoint}  ",
            ["Ai:ApiKey"] = "  key  ",
            ["Ai:Model"] = "  m  ",
            ["Ai:BackendLabel"] = "  Local (LM Studio)  ",
        });
        using var _ = provider;

        provider.GetRequiredService<AiPlausibilityBackendOptions>().ShouldBe(
            new AiPlausibilityBackendOptions(Configured: true, Label: "Local (LM Studio)", Model: "m"));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAiPlausibilityClient));
        client.BaseAddress.ShouldBe(new Uri(Endpoint));
        client.DefaultRequestHeaders.Authorization!.Parameter.ShouldBe("key");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://host")]
    [InlineData("/relative")]
    [InlineData("localhost:1234")]
    public void A_set_endpoint_that_is_not_an_absolute_http_or_https_url_fails_startup_naming_the_key(string endpoint)
    {
        var services = new ServiceCollection().AddLogging();
        var configuration = ConfigurationOf(new() { ["Ai:Endpoint"] = endpoint, ["Ai:ApiKey"] = "k-secret-value" });

        var exception = Should.Throw<InvalidOperationException>(
            () => AiPlausibilityBackendConfiguration.Configure(services, configuration));

        exception.Message.ShouldContain("Ai:Endpoint");
        exception.Message.ShouldNotContain("k-secret-value");
    }

    [Fact]
    public void The_legacy_AiPlausibility_keys_are_inert_so_the_documented_keys_are_the_only_keys()
    {
        // The audit's failure, inverted: before this story the code read these and ignored Ai:*.
        var (provider, result) = Build(new()
        {
            ["AiPlausibility:BaseUrl"] = Endpoint,
            ["AiPlausibility:ApiKey"] = "legacy",
            ["AiPlausibility:Model"] = "legacy",
            ["AiPlausibility:BackendLabel"] = "legacy",
        });
        using var _ = provider;

        provider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<NoOpAiPlausibilityClient>();
        provider.GetRequiredService<AiPlausibilityBackendOptions>().Configured.ShouldBeFalse();
        result.Configured.ShouldBeFalse();
    }

    [Fact]
    public void The_description_never_contains_the_api_key()
    {
        var (provider, result) = Build(new()
        {
            ["Ai:Endpoint"] = Endpoint,
            ["Ai:ApiKey"] = "k-secret-value",
            ["Ai:Model"] = "m",
            ["Ai:BackendLabel"] = "Local",
        });
        using var _ = provider;

        result.Description.ShouldNotContain("k-secret-value");
        result.Description.ShouldContain("OpenAiCompatibleClient");
        result.Description.ShouldContain(Endpoint);
        result.Description.ShouldContain("model m");
        result.Description.ShouldContain("Local");
    }

    [Theory]
    [InlineData("http://localhost:1234")]
    [InlineData("http://localhost:1234/")]
    [InlineData("https://api.example.test:8443")]
    public void A_server_root_endpoint_gets_no_warning(string endpoint)
    {
        var (provider, result) = Build(new() { ["Ai:Endpoint"] = endpoint });
        using var _ = provider;

        result.Warning.ShouldBeNull();
    }

    [Fact]
    public void No_endpoint_gets_no_warning()
    {
        var (provider, result) = Build(new());
        using var _ = provider;

        result.Warning.ShouldBeNull();
    }

    [Theory]
    [InlineData("https://host.example/openai")]
    [InlineData("https://api.example.test/v1/")]
    [InlineData("http://localhost:1234/?k=v")]
    [InlineData("http://localhost:1234/#frag")]
    [InlineData("https://user:pw-secret@host.example")]
    public void An_endpoint_with_more_than_the_server_root_still_starts_but_warns_without_echoing_it(string endpoint)
    {
        var (provider, result) = Build(new() { ["Ai:Endpoint"] = endpoint, ["Ai:ApiKey"] = "k-secret-value" });
        using var _ = provider;

        // Behaviour is unchanged: still the typed client, still configured.
        provider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<OpenAiCompatibleClient>();
        result.Configured.ShouldBeTrue();

        result.Warning.ShouldNotBeNull();
        result.Warning.ShouldContain("Ai:Endpoint");
        result.Warning.ShouldContain("server root");
        result.Warning.ShouldNotContain("openai");
        result.Warning.ShouldNotContain("pw-secret");
        result.Warning.ShouldNotContain("k=v");
        result.Warning.ShouldNotContain("k-secret-value");
    }

    [Fact]
    public void The_description_reduces_the_endpoint_to_scheme_host_and_port()
    {
        var (provider, result) = Build(new() { ["Ai:Endpoint"] = "https://person:secret@host.example:8443/v1/x?k=v" });
        using var _ = provider;

        result.Description.ShouldContain("https://host.example:8443");
        result.Description.ShouldNotContain("secret");
        result.Description.ShouldNotContain("person");
        result.Description.ShouldNotContain("k=v");
        result.Description.ShouldNotContain("/v1/x");
    }

    [Fact]
    public void A_default_port_is_left_out_of_the_description()
    {
        var (provider, result) = Build(new() { ["Ai:Endpoint"] = "https://api.example.test" });
        using var _ = provider;

        result.Description.ShouldContain("https://api.example.test");
        result.Description.ShouldNotContain(":443");
    }

    [Fact]
    public void The_real_appsettings_json_binds_to_the_keys_the_code_reads()
    {
        // The file's section name is the one the code reads: loading it and overlaying only the endpoint
        // must select the real client, and the file alone must select the no-op one.
        var appSettings = Path.Combine(FindRepoRoot(), "src", "EnergyTracker.Api", "appsettings.json");
        File.Exists(appSettings).ShouldBeTrue();

        var (offProvider, offResult) = BuildFrom(new ConfigurationBuilder().AddJsonFile(appSettings, optional: false).Build());
        using (offProvider)
        {
            offProvider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<NoOpAiPlausibilityClient>();
            offResult.Configured.ShouldBeFalse();
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appSettings, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:Endpoint"] = Endpoint })
            .Build();
        var (onProvider, onResult) = BuildFrom(configuration);
        using (onProvider)
        {
            onProvider.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<OpenAiCompatibleClient>();
            onResult.Configured.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task The_real_host_resolves_the_OpenAI_compatible_client_when_Ai_Endpoint_is_set()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseSetting("Ai:Endpoint", Endpoint));

        factory.Services.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<OpenAiCompatibleClient>();
        factory.Services.GetRequiredService<AiPlausibilityBackendOptions>().Configured.ShouldBeTrue();
    }

    [Fact]
    public async Task The_real_host_resolves_the_no_op_client_without_Ai_Endpoint()
    {
        await using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetRequiredService<IAiPlausibilityClient>().ShouldBeOfType<NoOpAiPlausibilityClient>();
        factory.Services.GetRequiredService<AiPlausibilityBackendOptions>().Configured.ShouldBeFalse();
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
