using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class SetHouseholdLocaleTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public async Task Persists_a_supported_locale_via_the_repository(string locale)
    {
        var householdId = Guid.NewGuid();
        var updated = new Household
        {
            Id = householdId,
            Locale = locale,
            Currency = "EUR",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _repository.UpdateLocaleAsync(householdId, locale, Arg.Any<CancellationToken>()).Returns(updated);
        var sut = new SetHouseholdLocale(_repository);

        var result = await sut.ExecuteAsync(householdId, locale, TestContext.Current.CancellationToken);

        result.ShouldBe(updated);
        await _repository.Received(1).UpdateLocaleAsync(householdId, locale, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("de-de")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Rejects_an_unsupported_locale_without_touching_the_repository(string? locale)
    {
        var sut = new SetHouseholdLocale(_repository);

        await Should.ThrowAsync<HouseholdValidationException>(() =>
            sut.ExecuteAsync(Guid.NewGuid(), locale, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().UpdateLocaleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
