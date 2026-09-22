using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnergyTracker.Api.Endpoints;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class HouseholdImportEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static async Task<HttpClient> CreateClientWithHouseholdAsync(EnergyTrackerApiFactory factory)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        return client;
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName = "export.json")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", fileName);
        return content;
    }

    private async Task<JobStatusResponse> PollJobToTerminalAsync(HttpClient client, Guid jobId)
    {
        // InProcessChannelJobProcessingService runs as a real hosted BackgroundService in this
        // test host — genuinely async, so polling (not an artificial delay) is correct here too
        // (mirrors SmartPlugImportEndpointsTests' own helper).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/jobs/{jobId}", TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var status = await response.Content.ReadFromJsonAsync<JobStatusResponse>(TestContext.Current.CancellationToken);
                if (status!.Status is "completed" or "failed")
                {
                    return status;
                }
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Job {jobId} did not reach a terminal status within the test deadline.");
    }

    [Fact]
    public async Task POST_household_import_returns_200_with_a_token_and_summary_for_a_valid_v2_file()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        using var upload = BuildUpload(exportBytes);
        var response = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("token").GetGuid().ShouldNotBe(Guid.Empty);
        body.GetProperty("summary").GetProperty("householdMembers").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task POST_household_import_rejects_a_non_v2_file_with_every_failure_reported()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var malformed = Encoding.UTF8.GetBytes("""{ "formatVersion": "v1" }""");

        using var upload = BuildUpload(malformed);
        var response = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var failures = body.GetProperty("failures");
        failures.GetArrayLength().ShouldBe(1);
        failures[0].GetString().ShouldNotBeNull().ShouldContain("v1");
    }

    [Fact]
    public async Task POST_household_import_rejects_a_file_with_many_structural_problems_reporting_all_of_them()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var malformed = Encoding.UTF8.GetBytes("""
            {
              "formatVersion": "v2",
              "household": { "id": "not-a-guid" }
            }
            """);

        using var upload = BuildUpload(malformed);
        var response = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var failures = body.GetProperty("failures");
        failures.GetArrayLength().ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_importing()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var upload = Encoding.UTF8.GetBytes("""{ "formatVersion": "v2" }""");

        using var uploadContent = BuildUpload(upload);
        var response = await client.PostAsync("/api/household-import", uploadContent, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task POST_confirm_with_an_unknown_token_is_not_found()
    {
        var client = await CreateClientWithHouseholdAsync(factory);

        var response = await client.PostAsync($"/api/household-import/{Guid.NewGuid()}/confirm", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_confirm_with_a_token_from_a_different_Household_is_not_found()
    {
        var owner = await CreateClientWithHouseholdAsync(factory);
        var exportResponse = await owner.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var upload = BuildUpload(exportBytes);
        var validateResponse = await owner.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        var validateBody = await validateResponse.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken);

        var outsider = await CreateClientWithHouseholdAsync(factory);
        var response = await outsider.PostAsync($"/api/household-import/{validateBody!.Token}/confirm", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // AC #1, #4, #6, end-to-end: upload+validate -> confirm -> async job completes -> the
    // Household's data actually round-trips (self-restore, the safest live scenario Task 8 itself
    // recommends).
    [Fact]
    public async Task Full_validate_then_confirm_flow_restores_the_Households_own_exported_data()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Kitchen" }, TestContext.Current.CancellationToken);
        roomResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue = 12345.6m, readingTimestamp = DateTimeOffset.UtcNow, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        using var upload = BuildUpload(exportBytes);
        var validateResponse = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var validateBody = await validateResponse.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken);

        var confirmResponse = await client.PostAsync($"/api/household-import/{validateBody!.Token}/confirm", null, TestContext.Current.CancellationToken);
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var confirmBody = await confirmResponse.Content.ReadFromJsonAsync<HouseholdImportConfirmResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, confirmBody!.JobId);
        terminalStatus.Status.ShouldBe("completed");
        terminalStatus.ErrorMessage.ShouldBeNull();

        // The session survives the restore (Dev Notes "session continuity") — Settings/Dashboard
        // reads still resolve via the same authenticated client.
        var afterRestoreExport = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        afterRestoreExport.StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterRestoreBody = await afterRestoreExport.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        afterRestoreBody.GetProperty("rooms").GetArrayLength().ShouldBe(1);
        afterRestoreBody.GetProperty("rooms")[0].GetProperty("name").GetString().ShouldBe("Kitchen");
        afterRestoreBody.GetProperty("meterReadings").GetArrayLength().ShouldBe(1);
        afterRestoreBody.GetProperty("meterReadings")[0].GetProperty("kwhValue").GetDecimal().ShouldBe(12345.6m);
    }

    // AC #4/#1: confirming the SAME token twice must not restore twice — the second confirm 404s
    // (the token is consumed atomically on first use).
    [Fact]
    public async Task Confirming_the_same_token_twice_the_second_time_is_not_found()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var upload = BuildUpload(exportBytes);
        var validateResponse = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        var validateBody = await validateResponse.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken);

        var firstConfirm = await client.PostAsync($"/api/household-import/{validateBody!.Token}/confirm", null, TestContext.Current.CancellationToken);
        firstConfirm.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var secondConfirm = await client.PostAsync($"/api/household-import/{validateBody.Token}/confirm", null, TestContext.Current.CancellationToken);

        secondConfirm.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
