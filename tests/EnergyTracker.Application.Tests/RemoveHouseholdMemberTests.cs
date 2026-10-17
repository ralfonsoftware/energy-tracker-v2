using EnergyTracker.Application.Ports;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RemoveHouseholdMemberTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();
    private readonly CapturingLogger<RemoveHouseholdMember> _logger = new();

    private RemoveHouseholdMember Sut() => new(_repository, _logger);

    [Fact]
    public async Task Surfaces_a_member_that_is_not_in_the_callers_Household_as_not_found()
    {
        var householdId = Guid.NewGuid();
        var target = Guid.NewGuid();
        _repository.RemoveMemberAsync(householdId, target, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HouseholdMemberNotFoundException(target)));

        await Should.ThrowAsync<HouseholdMemberNotFoundException>(() =>
            Sut().ExecuteAsync(householdId, Guid.NewGuid(), target, TestContext.Current.CancellationToken));

        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Surfaces_the_last_member_rule()
    {
        var householdId = Guid.NewGuid();
        var member = Guid.NewGuid();
        _repository.RemoveMemberAsync(householdId, member, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new LastHouseholdMemberException(householdId)));

        await Should.ThrowAsync<LastHouseholdMemberException>(() =>
            Sut().ExecuteAsync(householdId, member, member, TestContext.Current.CancellationToken));

        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removes_through_the_repository_once_with_the_callers_Household_and_the_target_id()
    {
        var householdId = Guid.NewGuid();
        var actingMember = Guid.NewGuid();
        var target = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        await Sut().ExecuteAsync(householdId, actingMember, target, TestContext.Current.CancellationToken);

        await _repository.Received(1).RemoveMemberAsync(
            householdId,
            target,
            Arg.Is<DateTimeOffset>(now => now >= before && now <= DateTimeOffset.UtcNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logs_who_removed_whom_with_ids_only()
    {
        var householdId = Guid.NewGuid();
        var actingMember = Guid.NewGuid();
        var target = Guid.NewGuid();

        await Sut().ExecuteAsync(householdId, actingMember, target, TestContext.Current.CancellationToken);

        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.State["HouseholdId"].ShouldBe(householdId);
        entry.State["RemovedMemberId"].ShouldBe(target);
        entry.State["RemovedByMemberId"].ShouldBe(actingMember);
        entry.State["Leaving"].ShouldBe(false);
        entry.State.Keys.Where(k => k != "{OriginalFormat}").OrderBy(k => k)
            .ShouldBe(["HouseholdId", "Leaving", "RemovedByMemberId", "RemovedMemberId"]);
    }

    [Fact]
    public async Task Marks_the_log_event_as_leaving_when_the_member_removes_themselves()
    {
        var householdId = Guid.NewGuid();
        var member = Guid.NewGuid();

        await Sut().ExecuteAsync(householdId, member, member, TestContext.Current.CancellationToken);

        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.State["Leaving"].ShouldBe(true);
        entry.State["RemovedMemberId"].ShouldBe(member);
        entry.State["RemovedByMemberId"].ShouldBe(member);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : [];
            Entries.Add(new LogEntry(logLevel, values));
        }
    }

    private sealed record LogEntry(LogLevel Level, Dictionary<string, object?> State);
}
