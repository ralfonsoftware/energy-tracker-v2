using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetTariffCheckReminderTests
{
    private readonly ITariffRepository _tariffRepository = Substitute.For<ITariffRepository>();

    private GetTariffCheckReminder Sut() => new(_tariffRepository);

    private static Tariff CurrentTariff(Guid householdId, DateTimeOffset contractStartDate, int contractPeriodMonths) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MonthlyBaseFee = 12.50m,
        PricePerKwh = 0.32m,
        Currency = "EUR",
        ContractStartDate = contractStartDate,
        ContractPeriodMonths = contractPeriodMonths,
        CreatedAtUtc = contractStartDate,
        Version = 0,
    };

    [Fact]
    public async Task No_current_Tariff_configured_returns_null()
    {
        var householdId = Guid.NewGuid();
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns((Tariff?)null);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task More_than_3_months_remain_before_the_minimum_term_ends_is_not_due()
    {
        var householdId = Guid.NewGuid();
        // Minimum term ends in 6 months (12-month contract starting 6 months ago) -> gate opens in
        // 3 months from now -> not yet due.
        var tariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddMonths(-6), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsDue.ShouldBeFalse();
    }

    [Fact]
    public async Task Exactly_at_the_3_month_boundary_the_gate_is_due_inclusive()
    {
        var householdId = Guid.NewGuid();
        // gateOpensAtUtc = ContractStartDate.AddMonths(ContractPeriodMonths).AddMonths(-3), so to
        // land gateOpensAtUtc at "now" the +3/-3 must NOT cancel against ContractStartDate itself:
        // ContractStartDate = now, ContractPeriodMonths = 3 -> minimum term ends in 3 months ->
        // gate opens 3 months before that -> gateOpensAtUtc == now. IsDue uses UtcNow >=
        // gateOpensAtUtc, and the tiny (sub-millisecond) delay between capturing `now` here and the
        // SUT's own UtcNow read means the real comparison instant is always fractionally after
        // gateOpensAtUtc, exercising the inclusive/just-past side of the boundary deterministically.
        var contractStartDate = DateTimeOffset.UtcNow;
        var tariff = CurrentTariff(householdId, contractStartDate, 3);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.GateOpensAtUtc.ShouldBe(contractStartDate);
        result.IsDue.ShouldBeTrue();
    }

    [Fact]
    public async Task One_day_before_the_gate_opens_is_not_yet_due()
    {
        var householdId = Guid.NewGuid();
        // Mirrors the boundary test above but one day on the not-yet-due side of gateOpensAtUtc,
        // to pin down the boundary from both directions.
        var contractStartDate = DateTimeOffset.UtcNow.AddDays(1);
        var tariff = CurrentTariff(householdId, contractStartDate, 3);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsDue.ShouldBeFalse();
    }

    [Fact]
    public async Task Minimum_term_elapsed_well_in_the_past_stays_due()
    {
        var householdId = Guid.NewGuid();
        // Minimum term ended 2 years ago -> AC #4, the gate stays open indefinitely (monotonic).
        var tariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddYears(-3), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.IsDue.ShouldBeTrue();
    }

    [Fact]
    public async Task GateOpensAtUtc_equals_ContractStartDate_plus_ContractPeriodMonths_minus_3_months()
    {
        var householdId = Guid.NewGuid();
        // Fixed dates and a hardcoded expected value (not the production formula recomputed) so a
        // wrong constant or flipped sign in GetTariffCheckReminder itself would actually fail this:
        // 2026-01-15 + 12 months = 2027-01-15 (minimum term end), minus 3 months = 2026-10-15.
        var contractStartDate = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var tariff = CurrentTariff(householdId, contractStartDate, 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.GateOpensAtUtc.ShouldBe(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Different_ContractStartDate_or_ContractPeriodMonths_produce_different_results_no_stale_schedule()
    {
        // AC #5: no separate persisted schedule exists to go stale — every call re-reads the
        // Tariff's current fields live, so two otherwise-identical calls differing only in these
        // two fields must produce different IsDue/GateOpensAtUtc.
        var householdId = Guid.NewGuid();
        var sut = Sut();

        var notYetDueTariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddMonths(-1), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(notYetDueTariff);
        var beforeEdit = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        var nowDueTariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddYears(-2), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(nowDueTariff);
        var afterEdit = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        beforeEdit.ShouldNotBeNull();
        afterEdit.ShouldNotBeNull();
        beforeEdit.IsDue.ShouldNotBe(afterEdit.IsDue);
        beforeEdit.GateOpensAtUtc.ShouldNotBe(afterEdit.GateOpensAtUtc);
    }

    [Fact]
    public async Task Editing_ContractStartDate_forward_on_an_already_due_Tariff_can_turn_IsDue_back_off()
    {
        // Not a violation of "monotonic" (AC #4 is about time alone never re-closing the gate for a
        // fixed Tariff) — this is AC #5's own "recompute against the new dates going forward"
        // behavior, since GetTariffCheckReminder has no persisted schedule to go stale. Pins down
        // the true -> false direction explicitly, complementing the false -> true direction already
        // covered above.
        var householdId = Guid.NewGuid();
        var sut = Sut();

        var dueTariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddYears(-2), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(dueTariff);
        var beforeEdit = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        var editedTariff = CurrentTariff(householdId, DateTimeOffset.UtcNow.AddMonths(-1), 12);
        _tariffRepository.FindCurrentForHouseholdAsync(householdId, Arg.Any<CancellationToken>()).Returns(editedTariff);
        var afterEdit = await sut.ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        beforeEdit.ShouldNotBeNull();
        afterEdit.ShouldNotBeNull();
        beforeEdit.IsDue.ShouldBeTrue();
        afterEdit.IsDue.ShouldBeFalse();
    }
}
