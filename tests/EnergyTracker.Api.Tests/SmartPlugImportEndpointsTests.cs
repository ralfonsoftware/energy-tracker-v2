using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class SmartPlugImportEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly string EveSampleFilePath = Path.Combine(
        AppContext.BaseDirectory, "sample-data", "eve", "2026-06-20_Steckdose-1_Gesamtverbrauch.xlsx");

    private async Task<(HttpClient Client, Guid HouseholdId)> CreateHouseholdAsync()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    private static MultipartFormDataContent BuildUpload(string filePath, string contentType = "application/octet-stream")
    {
        var bytes = File.ReadAllBytes(filePath);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(filePath));
        return content;
    }

    private async Task<JobStatusResponse> PollJobToTerminalAsync(HttpClient client, Guid jobId)
    {
        // InProcessChannelJobProcessingService runs as a real hosted BackgroundService in this
        // test host — genuinely async, so polling (not an artificial delay) is the correct way
        // to observe completion, exactly like the real client will (AC #2).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/jobs/{jobId}", TestContext.Current.CancellationToken);
            // A 404 here (as opposed to a mid-Household-mismatch test's deliberate 404) just means
            // the BackgroundJob row hasn't been inserted yet — this test's own upload races the
            // background processor's async dequeue-and-insert. Keep polling instead of failing on
            // the first miss; only a 404 that persists past the deadline below is a real problem.
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
    public async Task POST_smart_plug_imports_returns_202_immediately_and_the_job_later_completes()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/api/power-points", new { roomId = room!.Id, name = "Steckdose 1" }, TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);

        // 202 Accepted, no synchronous parsing (AC #1).
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        body!.JobId.ShouldNotBe(Guid.Empty);

        var terminalStatus = await PollJobToTerminalAsync(client, body.JobId);
        terminalStatus.Status.ShouldBe("completed");
        terminalStatus.ImportStatus.ShouldBe("completed");
        terminalStatus.ErrorMessage.ShouldBeNull();
        terminalStatus.CompletedAtUtc.ShouldNotBeNull();
        _ = householdId;
    }

    [Fact]
    public async Task Eve_Home_readings_survive_the_database_round_trip_without_losing_precision()
    {
        // Regression test: SmartPlugReadingConfiguration.KwhValue previously used scale 2, which
        // silently rounded every Eve Home reading (e.g. 0.00082, from an 0.82 Wh sample) to 0.00
        // on persistence. This asserts the value read back from the real database, not just the
        // in-memory parser output.
        var (client, householdId) = await CreateHouseholdAsync();

        using var upload = BuildUpload(EveSampleFilePath);
        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        await PollJobToTerminalAsync(client, body!.JobId);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var readings = await dbContext.SmartPlugReadings.IgnoreQueryFilters()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(TestContext.Current.CancellationToken);

        readings.ShouldNotBeEmpty();
        readings.ShouldContain(r => r.KwhValue > 0m);
    }

    [Fact]
    public async Task An_import_with_no_matching_Power_Point_completes_as_AwaitingPowerPointMapping()
    {
        // No Power Point named "Steckdose 1" exists in this Household at all.
        var (client, _) = await CreateHouseholdAsync();

        using var upload = BuildUpload(EveSampleFilePath);
        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, body!.JobId);

        terminalStatus.Status.ShouldBe("completed");
        terminalStatus.ImportStatus.ShouldBe("awaitingpowerpointmapping");
    }

    [Fact]
    public async Task A_device_tag_matching_Power_Points_in_two_different_Rooms_is_treated_as_unmatched()
    {
        // Power Point Name is only unique within its Room — two Rooms can each have a "Steckdose
        // 1". An ambiguous match must never be resolved by picking one arbitrarily.
        var (client, _) = await CreateHouseholdAsync();
        var roomAResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var roomA = await roomAResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var roomBResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Bedroom" }, TestContext.Current.CancellationToken);
        var roomB = await roomBResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/api/power-points", new { roomId = roomA!.Id, name = "Steckdose 1" }, TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/api/power-points", new { roomId = roomB!.Id, name = "Steckdose 1" }, TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, body!.JobId);

        terminalStatus.ImportStatus.ShouldBe("awaitingpowerpointmapping");
    }

    [Fact]
    public async Task An_archived_Power_Point_is_never_matched()
    {
        var (client, _) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Steckdose 1" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/power-points/{powerPoint!.Id}", TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, body!.JobId);

        terminalStatus.ImportStatus.ShouldBe("awaitingpowerpointmapping");
    }

    [Fact]
    public async Task An_unsupported_file_extension_is_rejected_with_400()
    {
        var (client, _) = await CreateHouseholdAsync();
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "notes.txt");

        var response = await client.PostAsync("/api/smart-plug-imports", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_uploading()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        using var upload = BuildUpload(EveSampleFilePath);

        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mapping_an_unmatched_import_to_an_existing_Power_Point_attaches_readings_and_completes_it()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "A different outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var uploadResponse = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var awaitingStatus = await PollJobToTerminalAsync(client, uploadBody!.JobId);
        awaitingStatus.ImportStatus.ShouldBe("awaitingpowerpointmapping");
        awaitingStatus.SmartPlugImportId.ShouldNotBeNull();

        var mappingResponse = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint!.Id },
            TestContext.Current.CancellationToken);

        mappingResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var mappingBody = await mappingResponse.Content.ReadFromJsonAsync<SmartPlugImportMappingResponse>(TestContext.Current.CancellationToken);
        mappingBody!.Status.ShouldBe("completed");

        var finalStatus = await client.GetFromJsonAsync<JobStatusResponse>($"/api/jobs/{uploadBody.JobId}", TestContext.Current.CancellationToken);
        finalStatus!.ImportStatus.ShouldBe("completed");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var readings = await dbContext.SmartPlugReadings.IgnoreQueryFilters()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(TestContext.Current.CancellationToken);
        readings.ShouldNotBeEmpty();
        readings.ShouldAllBe(r => r.PowerPointId == powerPoint.Id && r.PowerPointName == powerPoint.Name && r.RoomName == room.Name);
    }

    [Fact]
    public async Task Mapping_a_cross_Household_SmartPlugImportId_returns_404()
    {
        var (clientA, _) = await CreateHouseholdAsync();
        using var upload = BuildUpload(EveSampleFilePath);
        var uploadResponse = await clientA.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var awaitingStatus = await PollJobToTerminalAsync(clientA, uploadBody!.JobId);

        var (clientB, _) = await CreateHouseholdAsync();
        var roomResponse = await clientB.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await clientB.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        var mappingResponse = await clientB.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint!.Id },
            TestContext.Current.CancellationToken);

        mappingResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mapping_to_an_archived_Power_Point_returns_409()
    {
        var (client, _) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);
        await client.DeleteAsync($"/api/power-points/{powerPoint!.Id}", TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var uploadResponse = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var awaitingStatus = await PollJobToTerminalAsync(client, uploadBody!.JobId);

        var mappingResponse = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint.Id },
            TestContext.Current.CancellationToken);

        mappingResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Mapping_an_already_completed_import_a_second_time_returns_409()
    {
        var (client, _) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        using var upload = BuildUpload(EveSampleFilePath);
        var uploadResponse = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var awaitingStatus = await PollJobToTerminalAsync(client, uploadBody!.JobId);

        var firstMapping = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint!.Id },
            TestContext.Current.CancellationToken);
        firstMapping.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secondMapping = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint.Id },
            TestContext.Current.CancellationToken);

        secondMapping.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
