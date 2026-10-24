using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.13 (AD-24, AC #2, #8, #9): the real host runs the instance heartbeat. It writes one row for
// its own process, removes it when the host stops, never blocks startup or /health when the database
// side fails, and the row is operational data that no export carries.
public class InstanceHeartbeatHostTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private static async Task<int> RowsForAsync(WebApplicationFactory<Program> host, Guid instanceId)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.AppInstances.CountAsync(a => a.Id == instanceId, TestContext.Current.CancellationToken);
    }

    // The base fixture host started before the fixture migrated the database, so its first beat may have
    // failed (and is retried a minute later); a fresh host started afterwards beats against a migrated schema.
    private WebApplicationFactory<Program> NewHost(Action<IServiceCollection>? configure = null) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => configure?.Invoke(services)));

    [Fact]
    public async Task A_started_host_writes_exactly_one_AppInstances_row_for_its_own_process()
    {
        await using var host = NewHost();
        var identity = host.Services.GetRequiredService<AppInstanceIdentity>();

        var written = await WaitUntilAsync(async () => await RowsForAsync(host, identity.Id) == 1);

        written.ShouldBeTrue("no AppInstances row appeared for the started host");
        using var scope = host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>().AppInstances
            .AsNoTracking()
            .SingleAsync(a => a.Id == identity.Id, TestContext.Current.CancellationToken);
        row.StartedAtUtc.ShouldBe(identity.StartedAtUtc);
        row.LastSeenAtUtc.ShouldBeGreaterThanOrEqualTo(identity.StartedAtUtc);
    }

    [Fact]
    public async Task A_host_whose_heartbeat_repository_throws_on_every_call_still_starts_and_answers_health()
    {
        var repository = Substitute.For<IAppInstanceRepository>();
        repository.BeatAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the AppInstances table does not exist"));
        repository.DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the AppInstances table does not exist"));
        repository.DeleteOlderThanAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the AppInstances table does not exist"));
        repository.ListAliveOthersAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the AppInstances table does not exist"));
        await using var host = NewHost(services => services.AddScoped(_ => repository));
        var client = host.CreateClient();

        var beaten = await WaitUntilAsync(() => Task.FromResult(
            repository.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAppInstanceRepository.BeatAsync))));
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        beaten.ShouldBeTrue("the heartbeat never ran, so this test would prove nothing");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stopping_the_host_removes_its_own_row()
    {
        var host = NewHost();
        var identity = host.Services.GetRequiredService<AppInstanceIdentity>();
        (await WaitUntilAsync(async () => await RowsForAsync(host, identity.Id) == 1)).ShouldBeTrue("no row to remove");

        await host.DisposeAsync();

        // Read through the fixture's own host: the stopped host's services are gone.
        (await RowsForAsync(factory, identity.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task The_AppInstances_row_is_not_part_of_a_household_export()
    {
        var identity = factory.Services.GetRequiredService<AppInstanceIdentity>();
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotContain("AppInstance", Case.Insensitive);
        body.ShouldNotContain(identity.Id.ToString(), Case.Insensitive);
    }
}
