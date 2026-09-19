using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetEventHistoryTests
{
    private readonly IEventRepository _repository = Substitute.For<IEventRepository>();

    private GetEventHistory Sut() => new(_repository);

    private static Event NewEvent(DateTimeOffset occurredAt, DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        Description = "cooked 2h",
        OccurredAt = occurredAt,
        CreatedAtUtc = createdAtUtc,
    };

    [Fact]
    public async Task Returns_an_empty_page_for_a_Household_with_no_Events()
    {
        _repository.GetPageForHouseholdAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Event>(), 0));
        var sut = Sut();

        var result = await sut.ExecuteAsync(1, 20, TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(0);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Returns_items_ordered_reverse_chronologically_by_OccurredAt_as_supplied_by_the_repository()
    {
        var now = DateTimeOffset.UtcNow;
        var newer = NewEvent(now, now);
        var older = NewEvent(now.AddDays(-1), now.AddDays(-1));
        _repository.GetPageForHouseholdAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Event> { newer, older }, 2));
        var sut = Sut();

        var result = await sut.ExecuteAsync(1, 20, TestContext.Current.CancellationToken);

        result.Items.Select(e => e.Id).ShouldBe([newer.Id, older.Id]);
    }

    // The CreatedAtUtc tiebreaker for equal OccurredAt is enforced by the repository's ORDER BY;
    // this test only confirms the use case passes the repository's ordering through untouched.
    [Fact]
    public async Task Preserves_the_repositorys_CreatedAtUtc_tiebreak_order_for_equal_OccurredAt()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var createdLater = NewEvent(occurredAt, occurredAt.AddMinutes(5));
        var createdEarlier = NewEvent(occurredAt, occurredAt);
        _repository.GetPageForHouseholdAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Event> { createdLater, createdEarlier }, 2));
        var sut = Sut();

        var result = await sut.ExecuteAsync(1, 20, TestContext.Current.CancellationToken);

        result.Items.Select(e => e.Id).ShouldBe([createdLater.Id, createdEarlier.Id]);
    }

    [Fact]
    public async Task Pagination_math_is_correct_across_multiple_pages()
    {
        var events = Enumerable.Range(0, 5)
            .Select(i => NewEvent(DateTimeOffset.UtcNow.AddDays(-i), DateTimeOffset.UtcNow.AddDays(-i)))
            .ToList();
        _repository.GetPageForHouseholdAsync(2, 2, Arg.Any<CancellationToken>())
            .Returns((events.Skip(2).Take(2).ToList() as IReadOnlyList<Event>, events.Count));
        var sut = Sut();

        var result = await sut.ExecuteAsync(2, 2, TestContext.Current.CancellationToken);

        result.Page.ShouldBe(2);
        result.PageSize.ShouldBe(2);
        result.TotalCount.ShouldBe(5);
        result.Items.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_page_below_1(int page)
    {
        var sut = Sut();

        var ex = await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(page, 20, TestContext.Current.CancellationToken));
        ex.ErrorCode.ShouldBe("event.page_invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Rejects_a_pageSize_outside_1_to_100(int pageSize)
    {
        var sut = Sut();

        var ex = await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(1, pageSize, TestContext.Current.CancellationToken));
        ex.ErrorCode.ShouldBe("event.page_size_invalid");
    }

    [Fact]
    public async Task Rejects_a_page_that_overflows_int32_for_the_given_pageSize()
    {
        var sut = Sut();

        var ex = await Should.ThrowAsync<EventValidationException>(() =>
            sut.ExecuteAsync(int.MaxValue, 100, TestContext.Current.CancellationToken));
        ex.ErrorCode.ShouldBe("event.page_invalid");
    }
}
