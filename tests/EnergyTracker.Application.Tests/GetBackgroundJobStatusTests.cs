using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetBackgroundJobStatusTests
{
    private readonly IBackgroundJobRepository _repository = Substitute.For<IBackgroundJobRepository>();
    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();
    private readonly IBackgroundJobLifecycle _lifecycle = Substitute.For<IBackgroundJobLifecycle>();
    private readonly Guid _householdId = Guid.NewGuid();

    private GetBackgroundJobStatus Sut() => new(_repository, _smartPlugImportRepository, _lifecycle);

    [Fact]
    public async Task ExecuteAsync_fails_the_Households_stale_jobs_before_reading_the_job()
    {
        var jobId = Guid.NewGuid();
        var job = new BackgroundJob
        {
            Id = jobId, HouseholdId = _householdId, JobType = "UnknownJobType", Status = BackgroundJobStatus.Failed,
            ErrorMessage = "job-interrupted", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _repository.FindByIdAsync(_householdId, jobId, Arg.Any<CancellationToken>()).Returns(job);

        var result = await Sut().ExecuteAsync(_householdId, jobId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Job.ErrorMessage.ShouldBe("job-interrupted");
        Received.InOrder(() =>
        {
            _lifecycle.FailStaleAsync(_householdId, Arg.Any<CancellationToken>());
            _repository.FindByIdAsync(_householdId, jobId, Arg.Any<CancellationToken>());
        });
    }
}
