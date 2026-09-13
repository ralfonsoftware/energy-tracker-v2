using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class GetTariffHistoryTests
{
    private readonly ITariffRepository _tariffRepository = Substitute.For<ITariffRepository>();
    private readonly IAuditCorrectionRecorder _auditCorrectionRecorder = Substitute.For<IAuditCorrectionRecorder>();

    private GetTariffHistory Sut() => new(_tariffRepository, _auditCorrectionRecorder);

    private static Tariff NewTariff(Guid householdId, DateTimeOffset contractStartDate, decimal monthlyBaseFee = 10m) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = householdId,
        MonthlyBaseFee = monthlyBaseFee,
        PricePerKwh = 0.32m,
        Currency = "EUR",
        ContractStartDate = contractStartDate,
        ContractPeriodMonths = 12,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    public GetTariffHistoryTests()
    {
        _auditCorrectionRecorder.GetLatestPerFieldForEntitiesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), AuditCorrection>());
    }

    [Fact]
    public async Task Returns_items_ordered_by_ContractStartDate_descending_as_supplied_by_the_repository()
    {
        var householdId = Guid.NewGuid();
        var newer = NewTariff(householdId, DateTimeOffset.UtcNow);
        var older = NewTariff(householdId, DateTimeOffset.UtcNow.AddMonths(-12));
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { newer, older }, 2));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Select(i => i.Tariff.Id).ShouldBe([newer.Id, older.Id]);
    }

    [Fact]
    public async Task The_entry_whose_ContractStartDate_is_the_latest_not_in_the_future_is_flagged_IsCurrent()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var future = NewTariff(householdId, now.AddMonths(6));
        var current = NewTariff(householdId, now.AddMonths(-1));
        var past = NewTariff(householdId, now.AddMonths(-13));
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { future, current, past }, 3));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Tariff.Id == current.Id).IsCurrent.ShouldBeTrue();
        result.Items.Single(i => i.Tariff.Id == future.Id).IsCurrent.ShouldBeFalse();
        result.Items.Single(i => i.Tariff.Id == past.Id).IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public async Task EffectiveUntil_is_the_next_later_entrys_ContractStartDate_or_null_for_the_latest_entry()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewTariff(householdId, now.AddMonths(-24));
        var second = NewTariff(householdId, now.AddMonths(-12));
        var latest = NewTariff(householdId, now.AddMonths(-1));
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { latest, second, first }, 3));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        result.Items.Single(i => i.Tariff.Id == first.Id).EffectiveUntil.ShouldBe(second.ContractStartDate);
        result.Items.Single(i => i.Tariff.Id == second.Id).EffectiveUntil.ShouldBe(latest.ContractStartDate);
        result.Items.Single(i => i.Tariff.Id == latest.Id).EffectiveUntil.ShouldBeNull();
    }

    [Fact]
    public async Task EffectiveUntil_and_IsCurrent_are_computed_across_the_whole_history_even_when_the_next_entry_is_on_a_different_page()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewTariff(householdId, now.AddMonths(-24));
        var second = NewTariff(householdId, now.AddMonths(-12));
        // Page 2 of pageSize 1 returns only `first` (the oldest, descending order) — total is 2.
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 2, 1, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { first }, 2));
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 2, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { second, first }, 2));
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 2, 1, TestContext.Current.CancellationToken);

        result.Items.Single().EffectiveUntil.ShouldBe(second.ContractStartDate);
    }

    [Fact]
    public async Task A_correction_on_one_field_surfaces_under_its_own_FieldName_key_others_absent()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, DateTimeOffset.UtcNow);
        var correction = new AuditCorrection
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            EntityType = "Tariff",
            EntityId = tariff.Id,
            FieldName = "MonthlyBaseFee",
            OldValue = "10",
            NewValue = "12.50",
            CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { tariff }, 1));
        _auditCorrectionRecorder.GetLatestPerFieldForEntitiesAsync("Tariff", Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), AuditCorrection> { [(tariff.Id, "MonthlyBaseFee")] = correction });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        var entry = result.Items.Single();
        entry.Corrections.ShouldContainKey("MonthlyBaseFee");
        entry.Corrections["MonthlyBaseFee"].ShouldBe(correction);
        entry.Corrections.ShouldNotContainKey("PricePerKwh");
    }

    [Fact]
    public async Task Two_fields_corrected_in_the_same_submission_both_surface_independently()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, DateTimeOffset.UtcNow);
        var baseFeeCorrection = new AuditCorrection
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, EntityType = "Tariff", EntityId = tariff.Id,
            FieldName = "MonthlyBaseFee", OldValue = "10", NewValue = "15", CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        var priceCorrection = new AuditCorrection
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, EntityType = "Tariff", EntityId = tariff.Id,
            FieldName = "PricePerKwh", OldValue = "0.32", NewValue = "0.35", CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        _tariffRepository.GetHistoryForHouseholdAsync(householdId, 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<Tariff> { tariff }, 1));
        _auditCorrectionRecorder.GetLatestPerFieldForEntitiesAsync("Tariff", Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), AuditCorrection>
            {
                [(tariff.Id, "MonthlyBaseFee")] = baseFeeCorrection,
                [(tariff.Id, "PricePerKwh")] = priceCorrection,
            });
        var sut = Sut();

        var result = await sut.ExecuteAsync(householdId, 1, 20, TestContext.Current.CancellationToken);

        var entry = result.Items.Single();
        entry.Corrections.Count.ShouldBe(2);
        entry.Corrections["MonthlyBaseFee"].ShouldBe(baseFeeCorrection);
        entry.Corrections["PricePerKwh"].ShouldBe(priceCorrection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_page_below_1(int page)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), page, 20, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Rejects_a_pageSize_outside_1_to_100(int pageSize)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 1, pageSize, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_absurdly_large_page_is_rejected_before_it_can_overflow_Skip()
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), int.MaxValue, 100, TestContext.Current.CancellationToken));
    }
}
