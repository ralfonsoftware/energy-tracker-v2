using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class CreateHouseholdTests
{
    private readonly IHouseholdRepository _repository = Substitute.For<IHouseholdRepository>();

    public CreateHouseholdTests()
    {
        _repository.FindMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((HouseholdMember?)null);
    }

    [Theory]
    [InlineData("de-DE", "EUR")]
    [InlineData("en-US", "USD")]
    public async Task Creates_household_and_creator_member_for_a_supported_locale(string locale, string currency)
    {
        var sut = new CreateHousehold(_repository);

        var household = await sut.ExecuteAsync("https://issuer.example", "subject-1", locale, currency, TestContext.Current.CancellationToken);

        household.Locale.ShouldBe(locale);
        household.Currency.ShouldBe(currency);
        household.Id.ShouldNotBe(Guid.Empty);

        await _repository.Received(1).AddAsync(
            Arg.Is<Household>(h => h.Id == household.Id && h.Locale == locale && h.Currency == currency),
            Arg.Is<HouseholdMember>(m =>
                m.HouseholdId == household.Id &&
                m.ExternalIssuer == "https://issuer.example" &&
                m.ExternalSubjectId == "subject-1"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("fr-FR")]
    [InlineData("de")]
    public async Task Rejects_a_locale_outside_the_launch_set_instead_of_defaulting(string locale)
    {
        var sut = new CreateHousehold(_repository);

        await Should.ThrowAsync<HouseholdValidationException>(() =>
            sut.ExecuteAsync("https://issuer.example", "subject-1", locale, "EUR", TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Household>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("eur")]
    public async Task Rejects_a_currency_that_is_not_a_plausible_ISO_4217_code(string currency)
    {
        var sut = new CreateHousehold(_repository);

        await Should.ThrowAsync<HouseholdValidationException>(() =>
            sut.ExecuteAsync("https://issuer.example", "subject-1", "de-DE", currency, TestContext.Current.CancellationToken));

        await _repository.DidNotReceive().AddAsync(Arg.Any<Household>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_a_second_household_for_a_principal_that_already_has_one()
    {
        var existingHouseholdId = Guid.NewGuid();
        _repository.FindMemberAsync("https://issuer.example", "subject-1", Arg.Any<CancellationToken>())
            .Returns(new HouseholdMember
            {
                Id = Guid.NewGuid(),
                HouseholdId = existingHouseholdId,
                ExternalIssuer = "https://issuer.example",
                ExternalSubjectId = "subject-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        var sut = new CreateHousehold(_repository);

        var exception = await Should.ThrowAsync<HouseholdAlreadyExistsException>(() =>
            sut.ExecuteAsync("https://issuer.example", "subject-1", "de-DE", "EUR", TestContext.Current.CancellationToken));

        exception.ExistingHouseholdId.ShouldBe(existingHouseholdId);
        await _repository.DidNotReceive().AddAsync(Arg.Any<Household>(), Arg.Any<HouseholdMember>(), Arg.Any<CancellationToken>());
    }
}
