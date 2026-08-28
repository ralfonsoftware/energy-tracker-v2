using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ListSmartPlugImportJobsTests
{
    private readonly IBackgroundJobRepository _backgroundJobRepository = Substitute.For<IBackgroundJobRepository>();
    private readonly ISmartPlugImportRepository _smartPlugImportRepository = Substitute.For<ISmartPlugImportRepository>();
    private readonly Guid _householdId = Guid.NewGuid();

    public ListSmartPlugImportJobsTests()
    {
        _smartPlugImportRepository.FindAllByBackgroundJobIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SmartPlugImport>)[]);
        _backgroundJobRepository.FindMembersByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<HouseholdMember>)[]);
        _smartPlugImportRepository.ListGapsByImportIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SmartPlugImportGap>)[]);
    }

    private ListSmartPlugImportJobs Sut() => new(_backgroundJobRepository, _smartPlugImportRepository);

    private BackgroundJob MakeJob(BackgroundJobStatus status, Guid? queuedByHouseholdMemberId = null, string? errorMessage = null) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        JobType = JobTypes.ProcessSmartPlugImport,
        Status = status,
        OriginalFileName = "export.xlsx",
        QueuedByHouseholdMemberId = queuedByHouseholdMemberId,
        ErrorMessage = errorMessage,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        CompletedAtUtc = status is BackgroundJobStatus.Completed or BackgroundJobStatus.Failed ? DateTimeOffset.UtcNow : null,
    };

    private SmartPlugImport MakeImport(Guid backgroundJobId, SmartPlugImportStatus status) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = _householdId,
        BackgroundJobId = backgroundJobId,
        VendorFormat = SmartPlugVendorFormat.EveHome,
        OriginalFileName = "export.xlsx",
        Status = status,
        DeviceTag = "Fridge",
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        CompletedAtUtc = DateTimeOffset.UtcNow,
    };

    private void ReturnJobs(params BackgroundJob[] jobs) =>
        _backgroundJobRepository.ListByJobTypeAsync(_householdId, JobTypes.ProcessSmartPlugImport, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<BackgroundJob>)jobs);

    private void ReturnImports(params SmartPlugImport[] imports) =>
        _smartPlugImportRepository.FindAllByBackgroundJobIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SmartPlugImport>)imports);

    [Fact]
    public async Task Queued_job_derives_to_Waiting()
    {
        var job = MakeJob(BackgroundJobStatus.Queued);
        ReturnJobs(job);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem();
        result[0].State.ShouldBe(SmartPlugImportJobState.Waiting);
    }

    [Fact]
    public async Task Processing_job_derives_to_Processing()
    {
        var job = MakeJob(BackgroundJobStatus.Processing);
        ReturnJobs(job);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.Processing);
    }

    [Fact]
    public async Task Failed_job_derives_to_Error_and_carries_the_error_message()
    {
        var job = MakeJob(BackgroundJobStatus.Failed, errorMessage: "An unexpected error occurred while processing this import.");
        ReturnJobs(job);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.Error);
        result[0].ErrorMessage.ShouldBe("An unexpected error occurred while processing this import.");
    }

    [Fact]
    public async Task Completed_job_with_a_Completed_import_derives_to_Success()
    {
        var job = MakeJob(BackgroundJobStatus.Completed);
        var import = MakeImport(job.Id, SmartPlugImportStatus.Completed);
        ReturnJobs(job);
        ReturnImports(import);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.Success);
    }

    [Fact]
    public async Task Completed_job_with_an_AwaitingPowerPointMapping_import_derives_to_NeedsMapping()
    {
        var job = MakeJob(BackgroundJobStatus.Completed);
        var import = MakeImport(job.Id, SmartPlugImportStatus.AwaitingPowerPointMapping);
        ReturnJobs(job);
        ReturnImports(import);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.NeedsMapping);
        result[0].SmartPlugImportId.ShouldBe(import.Id);
        result[0].DeviceTag.ShouldBe(import.DeviceTag);
    }

    [Fact]
    public async Task Completed_job_with_a_FlaggedForReview_import_derives_to_FlaggedForReview_and_loads_its_gaps()
    {
        var job = MakeJob(BackgroundJobStatus.Completed);
        var import = MakeImport(job.Id, SmartPlugImportStatus.FlaggedForReview);
        var gap = new SmartPlugImportGap
        {
            Id = Guid.NewGuid(),
            HouseholdId = _householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = null,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow),
            EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview,
            EstimatedTotalKwh = null,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        ReturnJobs(job);
        ReturnImports(import);
        _smartPlugImportRepository.ListGapsByImportIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns([gap]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.FlaggedForReview);
        result[0].Gaps.ShouldHaveSingleItem();
        result[0].Gaps[0].ShouldBe(gap);
    }

    [Fact]
    public async Task Gaps_for_multiple_FlaggedForReview_rows_are_batched_in_one_call_not_one_per_row()
    {
        // Review-round-2 patch regression guard: this used to be a per-row ListGapsByImportIdAsync
        // call inside the loop (N+1), despite the same method batching the analogous import/
        // member lookups. Two Flagged for Review jobs must resolve their gaps via exactly one
        // ListGapsByImportIdsAsync call, each getting only its own gap back.
        var jobA = MakeJob(BackgroundJobStatus.Completed);
        var importA = MakeImport(jobA.Id, SmartPlugImportStatus.FlaggedForReview);
        var jobB = MakeJob(BackgroundJobStatus.Completed);
        var importB = MakeImport(jobB.Id, SmartPlugImportStatus.FlaggedForReview);
        var gapA = new SmartPlugImportGap
        {
            Id = Guid.NewGuid(), HouseholdId = _householdId, SmartPlugImportId = importA.Id, PowerPointId = null,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var gapB = new SmartPlugImportGap
        {
            Id = Guid.NewGuid(), HouseholdId = _householdId, SmartPlugImportId = importB.Id, PowerPointId = null,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow), EndDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview, EstimatedTotalKwh = null, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        ReturnJobs(jobA, jobB);
        ReturnImports(importA, importB);
        _smartPlugImportRepository.ListGapsByImportIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([gapA, gapB]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result.Single(r => r.SmartPlugImportId == importA.Id).Gaps.ShouldBe([gapA]);
        result.Single(r => r.SmartPlugImportId == importB.Id).Gaps.ShouldBe([gapB]);
        await _smartPlugImportRepository.Received(1).ListGapsByImportIdsAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(importA.Id) && ids.Contains(importB.Id)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_job_with_no_matching_SmartPlugImport_row_derives_to_Error_instead_of_silently_Success()
    {
        // Review-round-2 patch: a Completed BackgroundJob with no paired SmartPlugImport row
        // (legacy data, or a future JobType with no import concept) used to silently derive
        // Success — masking a data-integrity gap instead of surfacing it.
        var job = MakeJob(BackgroundJobStatus.Completed);
        ReturnJobs(job);
        // No matching import returned for this job's id (constructor default: empty imports list).
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].State.ShouldBe(SmartPlugImportJobState.Error);
    }

    [Fact]
    public async Task Resolves_QueuedByDisplayName_from_the_jobs_QueuedByHouseholdMemberId()
    {
        var memberId = Guid.NewGuid();
        var member = new HouseholdMember
        {
            Id = memberId,
            HouseholdId = _householdId,
            ExternalIssuer = "https://issuer.example",
            ExternalSubjectId = "subject-1",
            DisplayName = "Sam",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var job = MakeJob(BackgroundJobStatus.Queued, queuedByHouseholdMemberId: memberId);
        ReturnJobs(job);
        _backgroundJobRepository.FindMembersByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([member]);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].QueuedByDisplayName.ShouldBe("Sam");
    }

    [Fact]
    public async Task QueuedByDisplayName_is_null_when_the_job_has_no_QueuedByHouseholdMemberId()
    {
        var job = MakeJob(BackgroundJobStatus.Queued, queuedByHouseholdMemberId: null);
        ReturnJobs(job);
        var sut = Sut();

        var result = await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        result[0].QueuedByDisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task Sweeps_expired_records_before_reading_the_list()
    {
        // The sweep must run before ListByJobTypeAsync, so a job aged past 30 days is genuinely
        // absent from the returned list within the same call (AC #6) — asserted here by ordering,
        // since the repository mocks don't share real backing state.
        ReturnJobs();
        var sut = Sut();

        await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _smartPlugImportRepository.SweepExpiredAsync(_householdId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            _backgroundJobRepository.ListByJobTypeAsync(_householdId, JobTypes.ProcessSmartPlugImport, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Sweep_cutoff_is_30_days_before_now()
    {
        ReturnJobs();
        var sut = Sut();
        var before = DateTimeOffset.UtcNow.AddDays(-30);

        await sut.ExecuteAsync(_householdId, TestContext.Current.CancellationToken);

        var after = DateTimeOffset.UtcNow.AddDays(-30);
        await _smartPlugImportRepository.Received(1).SweepExpiredAsync(
            _householdId,
            Arg.Is<DateTimeOffset>(cutoff => cutoff >= before && cutoff <= after),
            Arg.Any<CancellationToken>());
    }
}
