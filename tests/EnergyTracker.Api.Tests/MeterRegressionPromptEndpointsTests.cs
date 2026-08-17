using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class MeterRegressionPromptEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    // No "list prompts" endpoint is exposed (only GET .../open) — same direct-DbContext pattern as
    // MeterReadingEndpointsTests.CountMeterReadingRowsAsync, needed here to discover a still-queued
    // (non-open) prompt's id for the 409 test below.
    private async Task<Guid> GetPromptIdForReadingAsync(Guid meterReadingId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var prompt = await dbContext.MeterRegressionPrompts.IgnoreQueryFilters()
            .SingleAsync(p => p.MeterReadingId == meterReadingId, TestContext.Current.CancellationToken);
        return prompt.Id;
    }

    private static Task<HttpResponseMessage> PostReadingAsync(HttpClient client, decimal kwhValue, DateTimeOffset readingTimestamp) =>
        client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Posting_a_lower_reading_then_GET_open_returns_it()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);

        var lowerResponse = await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var lowerReading = await lowerResponse.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var openResponse = await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken);
        openResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var open = await openResponse.Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);
        open.ShouldNotBeNull();
        open.MeterReadingId.ShouldBe(lowerReading!.Id);
        open.ReadingKwhValue.ShouldBe(412m);
        open.PreviousReadingKwhValue.ShouldBe(14302m);
    }

    [Fact]
    public async Task GET_open_returns_null_when_nothing_is_open()
    {
        var (client, _) = await CreateHouseholdAsync();
        await PostReadingAsync(client, 100m, DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Results.Ok(null) writes an empty body, not the JSON literal "null" — MeterRegressionApi.ts
        // (frontend) treats an empty body the same as null rather than relying on this literal text.
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolving_as_reset_returns_200_and_a_subsequent_GET_open_returns_null()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var resolveResponse = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open!.Id}/resolve",
            new { classification = "reset", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        resolveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resolved = await resolveResponse.Content.ReadFromJsonAsync<ResolveMeterRegressionPromptResponse>(TestContext.Current.CancellationToken);
        resolved!.Classification.ShouldBe("reset");

        var afterResponse = await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken);
        (await afterResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolving_as_rollover_with_a_capacity_persists_it_and_a_later_rollover_can_omit_it()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 99900m, baseline);
        await PostReadingAsync(client, 100m, baseline.AddHours(1));
        var firstOpen = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var firstResolve = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{firstOpen!.Id}/resolve",
            new { classification = "rollover", digitCapacityKwh = 100000m },
            TestContext.Current.CancellationToken);
        firstResolve.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A second, fresh regression on the same Main Meter — omitting digitCapacityKwh this time
        // should still succeed, using the value persisted onto MainMeter by the first resolution.
        await PostReadingAsync(client, 99950m, baseline.AddHours(2));
        await PostReadingAsync(client, 50m, baseline.AddHours(3));
        var secondOpen = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);
        secondOpen!.MainMeterDigitCapacityKwh.ShouldBe(100000m);

        var secondResolve = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{secondOpen.Id}/resolve",
            new { classification = "rollover", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        secondResolve.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_second_lower_reading_while_the_first_prompt_is_unresolved_queues_behind_it_by_timestamp_not_entry_order()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        // The earlier-by-timestamp regression is posted SECOND (out of insertion order) — AC #4.
        await PostReadingAsync(client, 100m, baseline.AddHours(2));
        var laterButFirstInsertedRegression = await PostReadingAsync(client, 50m, baseline.AddHours(1));
        var laterButFirstInsertedReading = await laterButFirstInsertedRegression.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        // The open prompt must be the one for the earlier ReadingTimestamp (hour+1), even though
        // it was inserted after the hour+2 regression.
        open!.MeterReadingId.ShouldBe(laterButFirstInsertedReading!.Id);
    }

    [Fact]
    public async Task Resolving_the_queued_non_open_prompt_directly_returns_409()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        var earlierRegressionResponse = await PostReadingAsync(client, 100m, baseline.AddHours(1));
        var laterRegressionResponse = await PostReadingAsync(client, 50m, baseline.AddHours(2));
        var earlierReading = await earlierRegressionResponse.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);
        var laterReading = await laterRegressionResponse.Content.ReadFromJsonAsync<MeterReadingResponse>(TestContext.Current.CancellationToken);

        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);
        open!.MeterReadingId.ShouldBe(earlierReading!.Id);

        var queuedPromptId = await GetPromptIdForReadingAsync(laterReading!.Id);

        var response = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{queuedPromptId}/resolve",
            new { classification = "reset", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resolving_an_already_resolved_prompt_returns_409_on_the_second_call()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var first = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open!.Id}/resolve",
            new { classification = "reset", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open.Id}/resolve",
            new { classification = "reset", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_household_cannot_resolve_another_households_prompt()
    {
        var (ownerClient, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(ownerClient, 14302m, baseline);
        await PostReadingAsync(ownerClient, 412m, baseline.AddHours(1));
        var open = await (await ownerClient.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var (otherClient, _) = await CreateHouseholdAsync();
        var crossHouseholdResolve = await otherClient.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open!.Id}/resolve",
            new { classification = "reset", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        // AD-3's query filter scopes FindByIdAsync to the caller's own Household — a cross-Household
        // id is indistinguishable from a nonexistent one (404), never a 403 that would leak existence.
        crossHouseholdResolve.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var stillOpen = await (await ownerClient.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);
        stillOpen.ShouldNotBeNull();
    }

    [Fact]
    public async Task GET_open_never_returns_another_households_prompt()
    {
        var (ownerClient, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(ownerClient, 14302m, baseline);
        await PostReadingAsync(ownerClient, 412m, baseline.AddHours(1));

        var (otherClient, _) = await CreateHouseholdAsync();
        var otherOpenResponse = await otherClient.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken);

        // AD-3's query filter scopes GetOpenForHouseholdAsync to the caller's own Household — the
        // owner's open prompt must never leak into another Household's poll.
        otherOpenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var otherOpenBody = await otherOpenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        otherOpenBody.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rollover_with_no_available_digit_capacity_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open!.Id}/resolve",
            new { classification = "rollover", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unrecognized_classification_value_returns_400()
    {
        var (client, _) = await CreateHouseholdAsync();
        // Anchored a day in the past — several of these tests offset by up to +3 hours from
        // `baseline` to establish reading order, and Story 2.4's ReadingTimestamp bounds
        // validation now rejects timestamps more than a few minutes in the future.
        var baseline = DateTimeOffset.UtcNow.AddDays(-1);
        await PostReadingAsync(client, 14302m, baseline);
        await PostReadingAsync(client, 412m, baseline.AddHours(1));
        var open = await (await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<MeterRegressionPromptResponse>(TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            $"/api/meter-regression-prompts/{open!.Id}/resolve",
            new { classification = "not-a-real-value", digitCapacityKwh = (decimal?)null },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_reading_the_open_prompt()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync("/api/meter-regression-prompts/open", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
