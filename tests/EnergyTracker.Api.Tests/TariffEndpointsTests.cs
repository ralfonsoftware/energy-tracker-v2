using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class TariffEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    private static Task<HttpResponseMessage> PostTariffAsync(HttpClient client, decimal monthlyBaseFee, decimal pricePerKwh, string currency, DateTimeOffset contractStartDate, int contractPeriodMonths = 12) =>
        client.PostAsJsonAsync(
            "/api/tariffs",
            new { monthlyBaseFee, pricePerKwh, currency, contractStartDate, contractPeriodMonths },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task POST_tariffs_returns_200_on_create()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostTariffAsync(client, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);
        body!.MonthlyBaseFee.ShouldBe(12.50m);
        body.PricePerKwh.ShouldBe(0.3200m);
        body.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task POST_tariffs_with_a_non_positive_pricePerKwh_is_rejected()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostTariffAsync(client, 12.50m, 0m, "EUR", DateTimeOffset.UtcNow);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_tariffs_with_an_invalid_currency_is_rejected()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostTariffAsync(client, 12.50m, 0.32m, "EU", DateTimeOffset.UtcNow);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_creating_a_Tariff()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_tariffs_returns_a_paginated_page_ordered_by_ContractStartDate_descending()
    {
        var (client, _) = await CreateHouseholdAsync();
        var baseline = DateTimeOffset.UtcNow.AddMonths(-12);
        var firstResponse = await PostTariffAsync(client, 10m, 0.30m, "EUR", baseline);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);
        var secondResponse = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/tariffs?page=1&pageSize=20", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<TariffHistoryPageResponse>(TestContext.Current.CancellationToken);
        page!.TotalCount.ShouldBe(2);
        page.Items.Select(i => i.Id).ShouldBe([secondBody!.Id, firstBody!.Id]);
        page.Items.Single(i => i.Id == secondBody.Id).IsCurrent.ShouldBeTrue();
        page.Items.Single(i => i.Id == firstBody.Id).EffectiveUntil.ShouldBe(secondBody.ContractStartDate);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_Tariff_history()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/tariffs?page=1&pageSize=20", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PUT_tariffs_id_edits_a_future_dated_entry_and_records_a_correction_note_visible_on_the_next_GET()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddMonths(1));
        var createdBody = await created.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var putResponse = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody!.Id}",
            new { monthlyBaseFee = 15m, version = createdBody.Version, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await putResponse.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);
        updated!.MonthlyBaseFee.ShouldBe(15m);

        var getResponse = await client.GetAsync("/api/tariffs?page=1&pageSize=20", TestContext.Current.CancellationToken);
        var page = await getResponse.Content.ReadFromJsonAsync<TariffHistoryPageResponse>(TestContext.Current.CancellationToken);
        var item = page!.Items.Single(i => i.Id == createdBody.Id);
        var correction = item.Corrections.Single(c => c.FieldName == "MonthlyBaseFee");
        correction.OldValue.ShouldBe("12.50");
        correction.NewValue.ShouldBe("15");
    }

    [Fact]
    public async Task PUT_tariffs_id_editing_a_price_field_on_an_already_started_entry_without_override_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(-10));
        var createdBody = await created.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody!.Id}",
            new { monthlyBaseFee = 20m, version = createdBody.Version, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PUT_tariffs_id_editing_a_price_field_on_an_already_started_entry_with_override_returns_200()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddDays(-10));
        var createdBody = await created.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody!.Id}",
            new { monthlyBaseFee = 20m, version = createdBody.Version, overrideConfirmed = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);
        updated!.MonthlyBaseFee.ShouldBe(20m);
    }

    [Fact]
    public async Task PUT_tariffs_id_with_a_stale_Version_returns_409_and_never_overwrites_the_first_writers_committed_value()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddMonths(1));
        var createdBody = await created.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var firstWriter = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody!.Id}",
            new { monthlyBaseFee = 15m, version = createdBody.Version, overrideConfirmed = false },
            TestContext.Current.CancellationToken);
        firstWriter.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secondWriter = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody.Id}",
            new { monthlyBaseFee = 20m, version = createdBody.Version, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        secondWriter.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var getResponse = await client.GetAsync("/api/tariffs?page=1&pageSize=20", TestContext.Current.CancellationToken);
        var page = await getResponse.Content.ReadFromJsonAsync<TariffHistoryPageResponse>(TestContext.Current.CancellationToken);
        page!.Items.Single(i => i.Id == createdBody.Id).MonthlyBaseFee.ShouldBe(15m);
    }

    [Fact]
    public async Task PUT_tariffs_id_for_an_entry_that_does_not_exist_returns_404()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/tariffs/{Guid.NewGuid()}",
            new { monthlyBaseFee = 15m, version = 0, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_editing_a_Tariff()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PutAsJsonAsync(
            $"/api/tariffs/{Guid.NewGuid()}",
            new { monthlyBaseFee = 15m, version = 0, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_households_Tariff_history_is_never_visible_to_another_household()
    {
        var (clientA, householdIdA) = await CreateHouseholdAsync();
        var (clientB, householdIdB) = await CreateHouseholdAsync();
        var createdA = await PostTariffAsync(clientA, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow);
        var createdABody = await createdA.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);
        await PostTariffAsync(clientB, 15m, 0.35m, "USD", DateTimeOffset.UtcNow);

        // AC #7/AD-3: B must never see A's Tariff via GET...
        var pageB = await (await clientB.GetAsync("/api/tariffs?page=1&pageSize=20", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<TariffHistoryPageResponse>(TestContext.Current.CancellationToken);
        pageB!.Items.ShouldAllBe(i => i.Currency == "USD");

        // ...nor edit it via PUT, even knowing its id.
        var editAttempt = await clientB.PutAsJsonAsync(
            $"/api/tariffs/{createdABody!.Id}",
            new { monthlyBaseFee = 999m, version = createdABody.Version, overrideConfirmed = true },
            TestContext.Current.CancellationToken);
        editAttempt.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify through Household A's own DbContext scope, not an ambient/unscoped one — Story
        // 3.9's own tenant-isolation review finding: querying through the wrong household's
        // context lets that household's AD-3 filter mask a false pass.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var stillOwnedByA = await dbContext.Tariffs.IgnoreQueryFilters()
            .SingleAsync(t => t.Id == createdABody.Id, TestContext.Current.CancellationToken);
        stillOwnedByA.HouseholdId.ShouldBe(householdIdA);
        stillOwnedByA.MonthlyBaseFee.ShouldBe(12.50m);
        householdIdB.ShouldNotBe(householdIdA);
    }
}
