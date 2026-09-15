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

    private static Task<HttpResponseMessage> PostCompareAsync(
        HttpClient client, decimal candidateMonthlyBaseFee, decimal candidatePricePerKwh, decimal candidateSwitchingBonus = 0m) =>
        client.PostAsJsonAsync(
            "/api/tariffs/compare",
            new { candidateMonthlyBaseFee, candidatePricePerKwh, candidateSwitchingBonus },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostReadingAsync(HttpClient client, decimal kwhValue, DateTimeOffset readingTimestamp) =>
        client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> SetYearlyBaselineAsync(HttpClient client, Guid householdId, decimal yearlyBaselineKwh, int version) =>
        client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline",
            new { yearlyBaselineKwh, version },
            TestContext.Current.CancellationToken);

    // Same shape as CreateHouseholdAsync but also returns the Household's own Version, needed to
    // set a Yearly Baseline (SetYearlyBaselineAsync) — a distinct helper rather than widening
    // CreateHouseholdAsync's tuple, which every other test in this file destructures as a 2-tuple.
    private async Task<(HttpClient Client, Guid HouseholdId, int Version)> CreateHouseholdWithPaceAsync(
        decimal yearlyBaselineKwh, decimal paceToDateKwh, double elapsedDays)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        (await SetYearlyBaselineAsync(client, created!.Id, yearlyBaselineKwh, created.Version)).EnsureSuccessStatusCode();

        // PaceToDateKwh is the delta between the two readings (StatusEndpointsTests' own
        // precedent), not the second reading's raw value — kWh values must also be positive
        // (MeterReadingValidation.ValidateKwhValue), so the first reading can't be a bare 0.
        // Truncated to whole seconds (not DateTimeOffset.UtcNow's full 100ns-tick precision) so
        // the elapsed span this test asserts an exact decimal result against can't drift after a
        // round trip through Postgres's microsecond-precision timestamptz column — the same
        // in-memory-vs-DB-truncated-timestamp flakiness class already hit and fixed once in this
        // repo (ee178cc).
        var now = DateTimeOffset.UtcNow;
        var latest = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset);
        var baseline = latest.AddDays(-elapsedDays);
        const decimal startingKwhValue = 1000m;
        (await PostReadingAsync(client, startingKwhValue, baseline)).EnsureSuccessStatusCode();
        (await PostReadingAsync(client, startingKwhValue + paceToDateKwh, latest)).EnsureSuccessStatusCode();

        return (client, created.Id, created.Version);
    }

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
        var secondItem = page.Items.Single(i => i.Id == secondBody.Id);
        secondItem.IsCurrent.ShouldBeTrue();
        // Compared against the GET response's own re-queried ContractStartDate, not the raw POST
        // response's in-memory value — Postgres's timestamptz column truncates to microsecond
        // precision, so a value round-tripped through the DB (as EffectiveUntil always is, via
        // GetHistoryForHouseholdAsync) can differ from an un-truncated in-memory DateTimeOffset by
        // a sub-microsecond amount, causing this assertion to flake intermittently.
        page.Items.Single(i => i.Id == firstBody.Id).EffectiveUntil.ShouldBe(secondItem.ContractStartDate);
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
    public async Task POST_tariffs_with_a_ContractStartDate_matching_an_existing_entry_is_rejected()
    {
        var (client, _) = await CreateHouseholdAsync();
        var contractStartDate = DateTimeOffset.UtcNow;
        await PostTariffAsync(client, 12.50m, 0.32m, "EUR", contractStartDate);

        var response = await PostTariffAsync(client, 15m, 0.35m, "EUR", contractStartDate);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PUT_tariffs_id_with_a_missing_version_returns_400_not_a_misleading_409()
    {
        var (client, _) = await CreateHouseholdAsync();
        var created = await PostTariffAsync(client, 12.50m, 0.32m, "EUR", DateTimeOffset.UtcNow.AddMonths(1));
        var createdBody = await created.Content.ReadFromJsonAsync<TariffResponse>(TestContext.Current.CancellationToken);

        var response = await client.PutAsJsonAsync(
            $"/api/tariffs/{createdBody!.Id}",
            new { monthlyBaseFee = 15m, overrideConfirmed = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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

    [Fact]
    public async Task POST_tariffs_compare_returns_200_with_a_computed_result_once_a_current_Tariff_and_pace_exist()
    {
        var (client, _, _) = await CreateHouseholdWithPaceAsync(yearlyBaselineKwh: 3650m, paceToDateKwh: 3200m, elapsedDays: 365);
        await PostTariffAsync(client, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow.AddYears(-1));

        var response = await PostCompareAsync(client, 14.90m, 0.3150m, 350m);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TariffComparisonResponse>(TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body.CurrentAnnualCost.ShouldBe(1174.00m);
        body.CandidateAnnualCostBonusNormalized.ShouldBe(1186.80m);
        body.BonusNormalizedAnnualSavings.ShouldBe(-12.80m);
        body.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task POST_tariffs_compare_returns_a_null_body_when_no_pace_exists_yet()
    {
        var (client, _) = await CreateHouseholdAsync();
        await PostTariffAsync(client, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow.AddYears(-1));

        var response = await PostCompareAsync(client, 14.90m, 0.3150m, 350m);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_tariffs_compare_returns_a_null_body_when_no_current_Tariff_exists()
    {
        var (client, _, _) = await CreateHouseholdWithPaceAsync(yearlyBaselineKwh: 3650m, paceToDateKwh: 3200m, elapsedDays: 365);

        var response = await PostCompareAsync(client, 14.90m, 0.3150m, 350m);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_tariffs_compare_with_a_non_positive_candidate_pricePerKwh_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostCompareAsync(client, 14.90m, 0m, 350m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_tariffs_compare_with_a_negative_switching_bonus_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostCompareAsync(client, 14.90m, 0.32m, -1m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_tariffs_compare_with_a_negative_candidateMonthlyBaseFee_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await PostCompareAsync(client, -1m, 0.32m, 0m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task POST_tariffs_compare_returns_a_null_body_with_exactly_one_reading()
    {
        // AC #3's literal boundary: fewer than two Meter Readings means no usable pace yet.
        // CreateHouseholdWithPaceAsync always seeds two readings, so this posts a single reading
        // directly instead.
        var (client, householdId) = await CreateHouseholdAsync();
        var householdResponse = await client.GetAsync($"/api/households/{householdId}", TestContext.Current.CancellationToken);
        var household = await householdResponse.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        (await SetYearlyBaselineAsync(client, householdId, 3650m, household!.Version)).EnsureSuccessStatusCode();
        (await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow)).EnsureSuccessStatusCode();
        await PostTariffAsync(client, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow.AddYears(-1));

        var response = await PostCompareAsync(client, 14.90m, 0.3150m, 350m);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_households_Tariff_comparison_is_never_affected_by_another_households_Tariff_or_pace()
    {
        var (clientA, _, _) = await CreateHouseholdWithPaceAsync(yearlyBaselineKwh: 3650m, paceToDateKwh: 3200m, elapsedDays: 365);
        await PostTariffAsync(clientA, 12.50m, 0.3200m, "EUR", DateTimeOffset.UtcNow.AddYears(-1));

        var (clientB, _, _) = await CreateHouseholdWithPaceAsync(yearlyBaselineKwh: 100m, paceToDateKwh: 50m, elapsedDays: 365);
        await PostTariffAsync(clientB, 999m, 9.99m, "USD", DateTimeOffset.UtcNow.AddYears(-1));

        var responseA = await PostCompareAsync(clientA, 14.90m, 0.3150m, 350m);

        responseA.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bodyA = await responseA.Content.ReadFromJsonAsync<TariffComparisonResponse>(TestContext.Current.CancellationToken);
        bodyA!.Currency.ShouldBe("EUR");
        bodyA.CurrentAnnualCost.ShouldBe(1174.00m);

        // Story 3.9's own tenant-isolation precedent: also query/assert through the *other*
        // household's own client, not just the acting one — proves B's own comparison reflects
        // only B's data too, not a false pass from only ever checking A's side.
        var responseB = await PostCompareAsync(clientB, 1000m, 10m, 0m);

        responseB.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bodyB = await responseB.Content.ReadFromJsonAsync<TariffComparisonResponse>(TestContext.Current.CancellationToken);
        bodyB!.Currency.ShouldBe("USD");
        bodyB.CurrentAnnualCost.ShouldBe(12487.50m);
    }
}
