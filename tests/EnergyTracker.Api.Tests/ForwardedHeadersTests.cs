using System.Net;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;

namespace EnergyTracker.Api.Tests;

/// <summary>
/// Overwrites the connecting peer's IP to a representative non-loopback (RFC 1918) address
/// before Program.cs's own middleware pipeline runs — not a claim that 10.0.0.4 matches Container
/// Apps' actual ingress peer address, which this diff doesn't document; any address outside
/// 127.0.0.0/8 serves the same purpose here. WebApplicationFactory's in-memory TestServer
/// otherwise connects as 127.0.0.1, which already falls inside ASP.NET Core's default
/// ForwardedHeadersOptions.KnownNetworks (127.0.0.0/8) — a test that skips this step would pass
/// even without Story 1.7's fix, testing nothing.
/// </summary>
public class FakeRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(next2 => async (HttpContext ctx) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.4");
            await next2(ctx);
        });
        next(app);
    };
}

public class ForwardedHeadersTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task X_Forwarded_Proto_and_X_Forwarded_For_are_trusted_from_a_non_loopback_peer_simulating_Container_Apps_ingress()
    {
        string? observedScheme = null;
        IPAddress? observedRemoteIp = null;
        await using var factory = CreateFactory(observe: (scheme, remoteIp) =>
        {
            observedScheme = scheme;
            observedRemoteIp = remoteIp;
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Story 1.7 AC #2: without KnownNetworks/KnownProxies cleared, a non-loopback peer's
        // X-Forwarded-Proto is silently ignored and this stays "http" — the exact production bug.
        observedScheme.ShouldBe("https");
        // AC #2 also names X-Forwarded-For: confirm it's actually applied to RemoteIpAddress,
        // not merely accepted on the wire without effect.
        observedRemoteIp.ShouldBe(IPAddress.Parse("203.0.113.5"));
    }

    private WebApplicationFactory<Program> CreateFactory(Action<string, IPAddress?>? observe = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());
            builder.ConfigureServices(services =>
            {
                // FakeRemoteIpStartupFilter must run first so the observer below sees the
                // post-middleware state with the faked peer IP already applied. IStartupFilter
                // wraps outside-in in registration order, so inserting at index 0 twice — filter
                // first, then observer — makes the observer the outermost wrapper: it runs its
                // "before next()" code last (irrelevant here) and its "after next()" code first,
                // i.e. immediately after the whole pipeline (including UseForwardedHeaders)
                // completes. Swapping this insertion order would silently invalidate the test.
                services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, FakeRemoteIpStartupFilter>());

                if (observe is not null)
                {
                    services.Insert(0, ServiceDescriptor.Transient<IStartupFilter>(_ =>
                        new ObservingStartupFilter(observe)));
                }
            });
        });

    private class ObservingStartupFilter(Action<string, IPAddress?> observe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(next2 => async (HttpContext ctx) =>
            {
                // Registered before Program.cs's own pipeline-building code runs, so this
                // middleware wraps AROUND it — reading these after calling next() observes the
                // values as they stand after UseForwardedHeaders has already run.
                await next2(ctx);
                observe(ctx.Request.Scheme, ctx.Connection.RemoteIpAddress);
            });
            next(app);
        };
    }
}
