using EnergyTracker.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class SessionEndpointsTests
{
    [Fact]
    public async Task Reports_no_federated_logout_support_when_the_OIDC_scheme_was_never_registered()
    {
        // Program.cs never calls AddOpenIdConnect when Oidc:Authority/ClientId are blank, leaving
        // ConfigurationManager null on the default-constructed options (Task 1).
        var options = new OpenIdConnectOptions();

        var result = await SessionEndpoints.ResolveSupportsFederatedLogoutAsync(options, TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Reports_federated_logout_support_when_the_discovery_document_advertises_an_end_session_endpoint()
    {
        var options = new OpenIdConnectOptions
        {
            ConfigurationManager = new FakeConfigurationManager(
                new OpenIdConnectConfiguration { EndSessionEndpoint = "https://issuer.example/oidc/logout" }),
        };

        var result = await SessionEndpoints.ResolveSupportsFederatedLogoutAsync(options, TestContext.Current.CancellationToken);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_no_federated_logout_support_when_the_discovery_document_omits_the_end_session_endpoint()
    {
        // Some minimal self-hosted OIDC servers omit end_session_endpoint (AD-17's FR-33 extension) —
        // this is the AC #3 fallback-warning trigger, distinct from "OIDC unconfigured".
        var options = new OpenIdConnectOptions
        {
            ConfigurationManager = new FakeConfigurationManager(new OpenIdConnectConfiguration()),
        };

        var result = await SessionEndpoints.ResolveSupportsFederatedLogoutAsync(options, TestContext.Current.CancellationToken);

        result.ShouldBeFalse();
    }

    private sealed class FakeConfigurationManager(OpenIdConnectConfiguration configuration) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }
    }
}
