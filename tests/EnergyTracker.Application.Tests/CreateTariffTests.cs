using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateTariffTests
{
    private readonly ITariffRepository _repository = Substitute.For<ITariffRepository>();

    private CreateTariff Sut() => new(_repository);

    [Fact]
    public async Task A_valid_Tariff_is_added_via_the_repository()
    {
        var householdId = Guid.NewGuid();
        _repository.AddAsync(Arg.Any<Tariff>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Tariff>());
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            householdId, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken);

        result.HouseholdId.ShouldBe(householdId);
        result.MonthlyBaseFee.ShouldBe(12.50m);
        result.PricePerKwh.ShouldBe(0.32m);
        result.Currency.ShouldBe("EUR");
        result.ContractPeriodMonths.ShouldBe(12);
        await _repository.Received(1).AddAsync(Arg.Any<Tariff>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1)]
    public async Task Rejects_a_negative_MonthlyBaseFee(decimal monthlyBaseFee)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), monthlyBaseFee, 0.32m, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Tariff>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_zero_MonthlyBaseFee_is_accepted()
    {
        _repository.AddAsync(Arg.Any<Tariff>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<Tariff>());
        var sut = Sut();

        var result = await sut.ExecuteAsync(
            Guid.NewGuid(), 0m, 0.32m, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken);

        result.MonthlyBaseFee.ShouldBe(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_PricePerKwh_that_is_not_positive(decimal pricePerKwh)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 12.50m, pricePerKwh, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("eur")]
    [InlineData("")]
    public async Task Rejects_a_currency_that_is_not_a_3_letter_uppercase_code(string currency)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 12.50m, 0.32m, currency, DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Rejects_a_ContractPeriodMonths_that_is_not_positive(int contractPeriodMonths)
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow, contractPeriodMonths, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_MonthlyBaseFee_that_would_overflow_the_decimal_18_2_column()
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 1_000_000_000_000_000m, 0.32m, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_PricePerKwh_that_would_overflow_the_decimal_18_4_column()
    {
        var sut = Sut();

        await Should.ThrowAsync<TariffValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), 12.50m, 10_000_000_000_000m, "EUR", DateTimeOffset.UtcNow, 12, TestContext.Current.CancellationToken));
    }
}
