using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Domain;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class JobEndpointsTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
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

    private async Task<Guid> UploadAndGetJobIdAsync(HttpClient client)
    {
        var bytes = File.ReadAllBytes(EveSampleFilePath);
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        content.Add(fileContent, "file", Path.GetFileName(EveSampleFilePath));

        var response = await client.PostAsync("/api/smart-plug-imports", content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        return body!.JobId;
    }

    [Fact]
    public async Task GET_jobs_id_for_an_id_that_never_existed_returns_404()
    {
        var (client, _) = await CreateHouseholdAsync();

        var response = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GET_jobs_id_for_a_job_belonging_to_a_different_Household_returns_404_not_403()
    {
        var (clientA, _) = await CreateHouseholdAsync();
        var jobId = await UploadAndGetJobIdAsync(clientA);
        var (clientB, _) = await CreateHouseholdAsync();

        // Existence must not leak across Households (AD-3, mirrors the Room/PowerPoint/Device
        // IDOR-guard pattern) — a job that exists for A is indistinguishable from one that never
        // existed at all when queried as B.
        var response = await clientB.GetAsync($"/api/jobs/{jobId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_principal_without_a_Household_is_forbidden_from_polling_a_job()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GET_jobs_id_for_a_Processing_job_with_a_stale_heartbeat_returns_failed_with_the_job_interrupted_code()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = await factory.SeedBackgroundJobAsync(
            householdId, "UnknownJobType", BackgroundJobStatus.Processing, now.AddMinutes(-30),
            startedAtUtc: now.AddMinutes(-30), heartbeatAtUtc: now.AddMinutes(-6));

        var response = await client.GetAsync($"/api/jobs/{jobId}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JobStatusResponse>(TestContext.Current.CancellationToken);
        body!.Status.ShouldBe("failed");
        body.ErrorMessage.ShouldBe("job-interrupted");
        body.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task GET_jobs_id_for_a_Processing_job_that_is_still_heartbeating_stays_processing()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var now = DateTimeOffset.UtcNow;
        var jobId = await factory.SeedBackgroundJobAsync(
            householdId, "UnknownJobType", BackgroundJobStatus.Processing, now.AddHours(-2),
            startedAtUtc: now.AddHours(-2), heartbeatAtUtc: now.AddSeconds(-20));

        var body = await (await client.GetAsync($"/api/jobs/{jobId}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JobStatusResponse>(TestContext.Current.CancellationToken);

        body!.Status.ShouldBe("processing");
        body.ErrorMessage.ShouldBeNull();
    }
}
