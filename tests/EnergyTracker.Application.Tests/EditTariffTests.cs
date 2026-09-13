using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class EditTariffTests
{
    private readonly ITariffRepository _tariffRepository = Substitute.For<ITariffRepository>();
    private readonly IAuditCorrectionRecorder _auditCorrectionRecorder = Substitute.For<IAuditCorrectionRecorder>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private EditTariff Sut()
    {
        // Pass-through: the real transaction wrapping is exercised by the API-layer Testcontainers
        // tests; here we just need the wrapped operation to actually run.
        _unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Tariff>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Func<CancellationToken, Task<Tariff>>>()(callInfo.Arg<CancellationToken>()));
        return new(_tariffRepository, _auditCorrectionRecorder, _unitOfWork);
    }

    private static Tariff NewTariff(
        Guid householdId, decimal monthlyBaseFee, decimal pricePerKwh, string currency, DateTimeOffset contractStartDate,
        int contractPeriodMonths = 12, int version = 0, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        HouseholdId = householdId,
        MonthlyBaseFee = monthlyBaseFee,
        PricePerKwh = pricePerKwh,
        Currency = currency,
        ContractStartDate = contractStartDate,
        ContractPeriodMonths = contractPeriodMonths,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = version,
    };

    [Fact]
    public async Task A_valid_edit_of_a_future_dated_entry_updates_the_field_and_increments_Version_with_no_override_needed()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(30), version: 3);
        var updated = NewTariff(householdId, 15m, 0.32m, "EUR", tariff.ContractStartDate, version: 4, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, 15m, null, null, null, null, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, tariff.Id, 15m, null, null, null, null, 3, overrideConfirmed: false, TestContext.Current.CancellationToken);

        result.MonthlyBaseFee.ShouldBe(15m);
        result.Version.ShouldBe(4);
    }

    [Fact]
    public async Task Editing_a_price_field_on_an_already_started_entry_without_override_confirmation_throws()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(-10), version: 0);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(householdId, tariff.Id, 20m, null, null, null, null, 0, overrideConfirmed: false, TestContext.Current.CancellationToken));

        await _tariffRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Guid>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_a_price_field_on_an_already_started_entry_with_override_confirmation_succeeds()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(-10), version: 0);
        var updated = NewTariff(householdId, 20m, 0.32m, "EUR", tariff.ContractStartDate, version: 1, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, 20m, null, null, null, null, 0, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, tariff.Id, 20m, null, null, null, null, 0, overrideConfirmed: true, TestContext.Current.CancellationToken);

        result.MonthlyBaseFee.ShouldBe(20m);
    }

    [Fact]
    public async Task Editing_a_non_price_field_on_an_already_started_entry_needs_no_override_confirmation()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(-10), version: 0);
        var updated = NewTariff(householdId, 12.50m, 0.32m, "USD", tariff.ContractStartDate, version: 1, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, null, null, "USD", null, null, 0, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, tariff.Id, null, null, "USD", null, null, 0, overrideConfirmed: false, TestContext.Current.CancellationToken);

        result.Currency.ShouldBe("USD");
    }

    [Fact]
    public async Task A_future_dated_entrys_price_field_needs_no_override_confirmation()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(10), version: 0);
        var updated = NewTariff(householdId, 20m, 0.32m, "EUR", tariff.ContractStartDate, version: 1, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, 20m, null, null, null, null, 0, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, tariff.Id, 20m, null, null, null, null, 0, overrideConfirmed: false, TestContext.Current.CancellationToken);

        result.MonthlyBaseFee.ShouldBe(20m);
    }

    [Fact]
    public async Task Editing_a_non_existent_or_foreign_household_entry_throws_TariffNotFoundException()
    {
        var tariffId = Guid.NewGuid();
        _tariffRepository.FindByIdAsync(tariffId, Arg.Any<CancellationToken>()).Returns((Tariff?)null);
        var sut = Sut();

        await Should.ThrowAsync<TariffNotFoundException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), tariffId, 20m, null, null, null, null, 0, overrideConfirmed: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_stale_Version_throws_TariffConcurrencyConflictException()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(10), version: 3);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, 20m, null, null, null, null, 2, Arg.Any<CancellationToken>())
            .Returns<Tariff>(_ => throw new TariffConcurrencyConflictException(tariff.Id));
        var sut = Sut();

        await Should.ThrowAsync<TariffConcurrencyConflictException>(() =>
            sut.ExecuteAsync(householdId, tariff.Id, 20m, null, null, null, null, 2, overrideConfirmed: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_no_op_edit_with_no_submitted_values_different_from_current_skips_the_write_and_never_calls_RecordAsync()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(10), version: 3);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, tariff.Id, 12.50m, null, null, null, null, 3, overrideConfirmed: false, TestContext.Current.CancellationToken);

        result.Version.ShouldBe(3);
        await _tariffRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Guid>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_is_called_once_per_changed_field_with_old_and_new_values_on_a_multi_field_edit()
    {
        var householdId = Guid.NewGuid();
        var contractStart = DateTimeOffset.UtcNow.AddDays(10);
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", contractStart, contractPeriodMonths: 12, version: 3);
        var updated = NewTariff(householdId, 15m, 0.35m, "EUR", contractStart, contractPeriodMonths: 12, version: 4, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        _tariffRepository.UpdateAsync(tariff.Id, 15m, 0.35m, null, null, null, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        await sut.ExecuteAsync(
            householdId, tariff.Id, 15m, 0.35m, null, null, null, 3, overrideConfirmed: false, TestContext.Current.CancellationToken);

        await _auditCorrectionRecorder.Received(1).RecordAsync(
            householdId, "Tariff", tariff.Id, "MonthlyBaseFee", "12.50", "15", Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.Received(1).RecordAsync(
            householdId, "Tariff", tariff.Id, "PricePerKwh", "0.32", "0.35", Arg.Any<CancellationToken>());
        // Currency wasn't submitted as a change — no correction row for it.
        await _auditCorrectionRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), "Currency", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resubmitting_a_field_with_its_own_current_value_is_not_treated_as_a_change()
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(10), version: 3);
        var updated = NewTariff(householdId, 12.50m, 0.35m, "EUR", tariff.ContractStartDate, version: 4, id: tariff.Id);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        // monthlyBaseFee submitted but unchanged (12.50m == current) -> repository call must pass
        // null for it, only pricePerKwh actually changed.
        _tariffRepository.UpdateAsync(tariff.Id, null, 0.35m, null, null, null, 3, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = Sut();

        await sut.ExecuteAsync(
            householdId, tariff.Id, 12.50m, 0.35m, null, null, null, 3, overrideConfirmed: false, TestContext.Current.CancellationToken);

        await _tariffRepository.Received(1).UpdateAsync(tariff.Id, null, 0.35m, null, null, null, 3, Arg.Any<CancellationToken>());
        await _auditCorrectionRecorder.DidNotReceive().RecordAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), "MonthlyBaseFee", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_submitted_PricePerKwh_that_is_not_positive(decimal pricePerKwh)
    {
        var householdId = Guid.NewGuid();
        var tariff = NewTariff(householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(10), version: 0);
        _tariffRepository.FindByIdAsync(tariff.Id, Arg.Any<CancellationToken>()).Returns(tariff);
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(householdId, tariff.Id, null, pricePerKwh, null, null, null, 0, overrideConfirmed: true, TestContext.Current.CancellationToken));
    }
}
