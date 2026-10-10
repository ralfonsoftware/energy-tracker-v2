using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CleanUpSmartPlugImportJobsTests
{
    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();
    private readonly IBackgroundJobRepository _backgroundJobRepository = Substitute.For<IBackgroundJobRepository>();
    private readonly Guid _householdId = Guid.NewGuid();

    private CleanUpSmartPlugImportJobs Sut() => new(_smartPlugImportRepository, _backgroundJobRepository);

    [Fact]
    public async Task ExecuteAsync_with_deleteAll_true_calls_the_repository_with_no_cutoff()
    {
        _smartPlugImportRepository.DeleteJobsAsync(_householdId, null, Arg.Any<CancellationToken>()).Returns(3);

        var deletedCount = await Sut().ExecuteAsync(_householdId, deleteAll: true, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(3);
        await _smartPlugImportRepository.Received(1).DeleteJobsAsync(_householdId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_with_deleteAll_false_calls_the_repository_with_a_cutoff_thirty_days_ago()
    {
        DateTimeOffset? capturedCutoff = null;
        _smartPlugImportRepository.DeleteJobsAsync(_householdId, Arg.Do<DateTimeOffset?>(c => capturedCutoff = c), Arg.Any<CancellationToken>())
            .Returns(5);

        var deletedCount = await Sut().ExecuteAsync(_householdId, deleteAll: false, TestContext.Current.CancellationToken);

        deletedCount.ShouldBe(5);
        capturedCutoff.ShouldNotBeNull();
        var expected = DateTimeOffset.UtcNow.AddDays(-30);
        // Small tolerance for the elapsed time between computing `expected` here and inside ExecuteAsync.
        Math.Abs((capturedCutoff!.Value - expected).TotalSeconds).ShouldBeLessThan(5);
    }

    [Fact]
    public async Task ExecuteAsync_with_deleteAll_true_also_removes_every_terminal_CorrelateEvent_row_until_a_call_returns_zero()
    {
        _backgroundJobRepository.DeleteTerminalByJobTypeAsync(_householdId, JobTypes.CorrelateEvent, null, Arg.Any<CancellationToken>())
            .Returns(200, 7, 0);

        await Sut().ExecuteAsync(_householdId, deleteAll: true, TestContext.Current.CancellationToken);

        await _backgroundJobRepository.Received(3).DeleteTerminalByJobTypeAsync(_householdId, JobTypes.CorrelateEvent, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_with_deleteAll_false_removes_terminal_CorrelateEvent_rows_older_than_the_retention_window()
    {
        var cutoffs = new List<DateTimeOffset?>();
        _backgroundJobRepository
            .DeleteTerminalByJobTypeAsync(_householdId, JobTypes.CorrelateEvent, Arg.Do<DateTimeOffset?>(cutoffs.Add), Arg.Any<CancellationToken>())
            .Returns(3, 0);

        await Sut().ExecuteAsync(_householdId, deleteAll: false, TestContext.Current.CancellationToken);

        cutoffs.Count.ShouldBe(2);
        foreach (var cutoff in cutoffs)
        {
            cutoff.ShouldNotBeNull();
            Math.Abs((cutoff!.Value - DateTimeOffset.UtcNow.AddDays(-30)).TotalSeconds).ShouldBeLessThan(5);
        }
    }
}
