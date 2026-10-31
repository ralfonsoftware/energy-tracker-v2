using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

    // Replaces the export's householdMembers with the given raw JSON array text — used to hand-edit
    // a "backup file" the way an uploader (or attacker) could.
    private static byte[] WithHouseholdMembers(byte[] exportBytes, string membersJson)
    {
        var root = JsonNode.Parse(exportBytes)!.AsObject();
        root["householdMembers"] = JsonNode.Parse(membersJson);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    // AD-25 / audit S1, end-to-end: a pre-change (legacy-shape) file that lists a foreign OIDC
    // identity and omits the caller is accepted, restores fine, and changes nothing about who has
    // access — the caller still resolves on the very next request and membership is unchanged.
    [Fact]
    public async Task Restoring_a_legacy_file_with_a_foreign_member_and_without_the_caller_leaves_membership_unchanged()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var membersBefore = JsonNode.Parse(exportBytes)!["householdMembers"]!.AsArray();
        membersBefore.Count.ShouldBe(1);
        var callerMemberId = membersBefore[0]!["id"]!.GetValue<Guid>();

        var foreignMemberId = Guid.NewGuid();
        var tampered = WithHouseholdMembers(exportBytes, $$"""
            [{
              "id": "{{foreignMemberId}}", "externalIssuer": "https://attacker.test/", "externalSubjectId": "evil|mallory",
              "displayName": "Mallory", "createdAtUtc": "2026-01-05T08:00:00+00:00"
            }]
            """);

        using var upload = BuildUpload(tampered);
        var validateResponse = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        validateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var validateBody = await validateResponse.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken);
        var confirmResponse = await client.PostAsync($"/api/household-import/{validateBody!.Token}/confirm", null, TestContext.Current.CancellationToken);
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var confirmBody = await confirmResponse.Content.ReadFromJsonAsync<HouseholdImportConfirmResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, confirmBody!.JobId);
        terminalStatus.Status.ShouldBe("completed");

        var afterResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        afterResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterMembers = (await afterResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("householdMembers");
        afterMembers.GetArrayLength().ShouldBe(1);
        afterMembers[0].GetProperty("id").GetGuid().ShouldBe(callerMemberId);
        afterMembers[0].GetProperty("id").GetGuid().ShouldNotBe(foreignMemberId);
    }

    // AC #4: the validate response carries the file's member display names (file order, trimmed,
    // blanks dropped) for the "People in this backup" panel; the count keeps counting everyone.
    [Fact]
    public async Task POST_household_import_summary_lists_the_files_member_display_names()
    {
        var client = await CreateClientWithHouseholdAsync(factory);
        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var edited = WithHouseholdMembers(exportBytes, $$"""
            [
              { "id": "{{Guid.NewGuid()}}", "displayName": "  Alice " },
              { "id": "{{Guid.NewGuid()}}", "displayName": null },
              { "id": "{{Guid.NewGuid()}}", "displayName": "   " },
              { "id": "{{Guid.NewGuid()}}" },
              { "id": "{{Guid.NewGuid()}}", "displayName": "Bob" }
            ]
            """);

        using var upload = BuildUpload(edited);
        var response = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var summary = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("summary");
        summary.GetProperty("householdMembers").GetInt32().ShouldBe(5);
        summary.GetProperty("memberDisplayNames").EnumerateArray().Select(e => e.GetString()).ShouldBe(["Alice", "Bob"]);
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

    private static async Task<(HttpClient Client, Guid HouseholdId)> CreateClientWithHouseholdIdAsync(EnergyTrackerApiFactory factory)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());
        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync<HouseholdResponse>(TestContext.Current.CancellationToken);
        return (client, created!.Id);
    }

    private static async Task<HttpResponseMessage> ValidateAndConfirmAsync(HttpClient client)
    {
        var exportResponse = await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken);
        var exportBytes = await exportResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var upload = BuildUpload(exportBytes);
        var validateResponse = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        var validateBody = await validateResponse.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken);
        return await client.PostAsync($"/api/household-import/{validateBody!.Token}/confirm", null, TestContext.Current.CancellationToken);
    }

    // Story 11.2 / AC #4: a restore row orphaned by a restart blocked the Household forever (C8).
    // The startup sweep is invoked explicitly: EnergyTrackerApiFactory builds the host (and so runs
    // the automatic sweep) before MigrateAsync, so that automatic sweep has nothing to sweep here.
    [Fact]
    public async Task Confirm_is_rejected_while_an_orphaned_restore_row_exists_and_accepted_after_the_startup_sweep()
    {
        var (client, householdId) = await CreateClientWithHouseholdIdAsync(factory);
        await factory.SeedBackgroundJobAsync(
            householdId, JobTypes.RestoreHouseholdData, BackgroundJobStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-10));

        (await ValidateAndConfirmAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Simulates "the process restarted": every row created before now belonged to the previous process.
        var sweep = factory.Services.GetRequiredService<InProcessJobStartupSweep>();
        await sweep.SweepAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var confirm = await ValidateAndConfirmAsync(client);
        confirm.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var confirmBody = await confirm.Content.ReadFromJsonAsync<HouseholdImportConfirmResponse>(TestContext.Current.CancellationToken);
        (await PollJobToTerminalAsync(client, confirmBody!.JobId)).Status.ShouldBe("completed");
    }

    // AC #3: a long restore that is still heartbeating keeps the 409 guard for its whole run ...
    [Fact]
    public async Task Confirm_stays_rejected_for_a_Processing_restore_that_is_still_heartbeating_however_old_it_is()
    {
        var (client, householdId) = await CreateClientWithHouseholdIdAsync(factory);
        var now = DateTimeOffset.UtcNow;
        await factory.SeedBackgroundJobAsync(
            householdId, JobTypes.RestoreHouseholdData, BackgroundJobStatus.Processing, now.AddMinutes(-10),
            startedAtUtc: now.AddMinutes(-10), heartbeatAtUtc: now.AddSeconds(-30));

        (await ValidateAndConfirmAsync(client)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ... and one whose heartbeat is older than five minutes is failed on the first read.
    [Fact]
    public async Task Confirm_fails_a_Processing_restore_with_a_stale_heartbeat_and_is_then_accepted()
    {
        var (client, householdId) = await CreateClientWithHouseholdIdAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var staleJobId = await factory.SeedBackgroundJobAsync(
            householdId, JobTypes.RestoreHouseholdData, BackgroundJobStatus.Processing, now.AddMinutes(-10),
            startedAtUtc: now.AddMinutes(-10), heartbeatAtUtc: now.AddMinutes(-6));

        var confirm = await ValidateAndConfirmAsync(client);

        confirm.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var stale = await (await client.GetAsync($"/api/jobs/{staleJobId}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JobStatusResponse>(TestContext.Current.CancellationToken);
        stale!.Status.ShouldBe("failed");
        stale.ErrorMessage.ShouldBe("job-interrupted");
    }

    // Story 11.14 (audit C10, AC #2/#8b): two confirms with different tokens that BOTH get past the early
    // check (it is made blind, as it is in the real race) reach the enqueue together. The database index is
    // the arbiter: exactly one answers 202, the other the same 409 the early check gives, exactly one active
    // row exists, and the loser's temp file is gone (the winner's stays for its job).
    [Fact]
    public async Task Two_confirms_with_different_tokens_past_a_blind_early_check_answer_one_202_and_one_409_and_leave_one_active_row_and_one_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new ParallelEnqueueGate(JobTypes.RestoreHouseholdData, callers: 2);
        using var directory = new UploadDirectory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new HouseholdImportUploadSettings(directory.Path, HouseholdImportEndpoints.MaxFileSizeBytes));
            gate.Install(services);
        }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "fetch");
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);
        var created = await (await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, ct))
            .Content.ReadFromJsonAsync<HouseholdResponse>(ct);
        var export = await ExportBytesAsync(client);

        try
        {
            var first = await UploadValidAsync(client, export);
            var firstConfirm = client.PostAsync($"/api/household-import/{first.Token}/confirm", null, ct);
            // The first token is consumed and its request is parked at the blind check, so the registry
            // takes a second pending upload for the same Household.
            await gate.WaitForArrivalsAsync(1, ct);
            var second = await UploadValidAsync(client, export);
            var secondConfirm = client.PostAsync($"/api/household-import/{second.Token}/confirm", null, ct);

            var responses = await Task.WhenAll(firstConfirm, secondConfirm);

            responses.Select(r => (int)r.StatusCode).Order().ShouldBe([(int)HttpStatusCode.Accepted, (int)HttpStatusCode.Conflict]);
            var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            (await loser.Content.ReadAsStringAsync(ct)).ShouldContain("A restore is already in progress for this Household");
            var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.Accepted)
                .Content.ReadFromJsonAsync<HouseholdImportConfirmResponse>(ct);
            using (var scope = host.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
                var active = await dbContext.BackgroundJobs.IgnoreQueryFilters().AsNoTracking()
                    .Where(j => j.HouseholdId == created!.Id && j.JobType == JobTypes.RestoreHouseholdData
                        && (j.Status == BackgroundJobStatus.Queued || j.Status == BackgroundJobStatus.Processing))
                    .ToListAsync(ct);
                active.ShouldHaveSingleItem().Id.ShouldBe(winner!.JobId);
            }

            // The winner's file waits for its held job; the loser's file was deleted by the confirm path.
            directory.Files.Length.ShouldBe(1);

            gate.ReleaseJobs();
            (await PollJobToTerminalAsync(client, winner.JobId)).Status.ShouldBe("completed");
        }
        finally
        {
            gate.ReleaseJobs();
        }
    }

    // --- Story 11.10 (audit S4): the temp file must never outlive a request that did not end in a
    // successful Register. The upload is streamed to disk before it is validated, so every exit needs
    // its own proof. Direct calls to ReceiveUploadAsync reach paths a real request cannot be made to
    // take (a failing source stream, a cancellation inside the copy). ---

    private sealed class UploadDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"import-tests-{Guid.NewGuid()}")).FullName;

        public string[] Files => Directory.GetFiles(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static FormFile FormFileOf(Stream stream, string fileName = "export.json") =>
        new(stream, 0, stream.Length, "file", fileName);

    private static FormFile FormFileOf(string json) => FormFileOf(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static HouseholdImportUploadSettings SettingsFor(UploadDirectory directory, long maxFileSizeBytes = HouseholdImportEndpoints.MaxFileSizeBytes) =>
        new(directory.Path, maxFileSizeBytes);

    private static Task<IResult> ReceiveAsync(
        IFormFile file, UploadDirectory directory, IHouseholdImportUploadRegistry? registry = null, CancellationToken? cancellationToken = null,
        long maxFileSizeBytes = HouseholdImportEndpoints.MaxFileSizeBytes, Guid? householdId = null) =>
        HouseholdImportEndpoints.ReceiveUploadAsync(
            file, householdId ?? Guid.NewGuid(), new ValidateHouseholdImport(), registry ?? new MemoryHouseholdImportUploadRegistry(TimeProvider.System),
            SettingsFor(directory, maxFileSizeBytes), cancellationToken ?? TestContext.Current.CancellationToken);

    private const string MinimalValidDocument =
        """
        {"formatVersion":"v2","exportedAtUtc":"2026-09-22T10:00:00+00:00",
         "household":{"id":"11111111-1111-1111-1111-111111111111","createdAtUtc":"2026-01-05T08:00:00+00:00","locale":"de-DE","currency":"EUR",
           "yearlyBaselineKwh":null,"trendingThresholdKwh":100.0,"lowConfidenceGapDays":45,"tariffCheckCadenceMonths":3,
           "aiPlausibilityEnabled":false,"aiPlausibilityBackendConfigured":false,"aiPlausibilityBackendLabel":null},
         "householdMembers":[],"mainMeter":null,"meterReadings":[],"meterRegressionPrompts":[],"tariffs":[],"events":[],"rooms":[],
         "powerPoints":[],"devices":[],"smartPlugReadings":[],"statusSnapshots":[],"auditCorrections":[]}
        """;

    private sealed class FailingAfterStream(byte[] bytes, int failAfter) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter)
            {
                throw new IOException("disk on fire");
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter)], cancellationToken);
        }
    }

    [Fact]
    public async Task A_validation_failure_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();

        var result = await ReceiveAsync(FormFileOf("""{ "formatVersion": "v1" }"""), directory, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_JSON_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();

        var result = await ReceiveAsync(FormFileOf("{ not valid json"), directory, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_too_large_check_answers_400_and_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();

        var result = await ReceiveAsync(FormFileOf(MinimalValidDocument), directory, cancellationToken: TestContext.Current.CancellationToken, maxFileSizeBytes: 10);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_request_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReceiveAsync(FormFileOf(MinimalValidDocument), directory, cancellationToken: cancelled.Token));

        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_exception_while_copying_the_upload_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();
        var bytes = Encoding.UTF8.GetBytes(MinimalValidDocument);

        await Should.ThrowAsync<IOException>(() => ReceiveAsync(FormFileOf(new FailingAfterStream(bytes, bytes.Length / 2)), directory));

        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_exception_from_the_registry_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();
        var registry = Substitute.For<IHouseholdImportUploadRegistry>();
        registry.When(r => r.Register(Arg.Any<Guid>(), Arg.Any<HouseholdImportUploadReference>())).Do(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => ReceiveAsync(FormFileOf(MinimalValidDocument), directory, registry));

        directory.Files.ShouldBeEmpty();
    }


    // An IFormFile whose stream cancels the request when the copy has finished and closed it, so the
    // cancellation lands between the copy and the validation, a window a real disconnect can hit.
    private sealed class CancelAfterCopyFormFile(byte[] bytes, CancellationTokenSource source) : IFormFile
    {
        public string ContentType => "application/json";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => bytes.Length;
        public string Name => "file";
        public string FileName => "export.json";
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Stream OpenReadStream() => new CancelOnDisposeStream(bytes, source);

        private sealed class CancelOnDisposeStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    source.Cancel();
                }

                base.Dispose(disposing);
            }
        }
    }

    // Code review of Story 11.10 / AC #9a: a client that disconnects while its file validates must not
    // replace the Household's earlier, good pending upload with a token nobody will receive.
    [Fact]
    public async Task A_request_cancelled_after_the_copy_leaves_no_temp_file_and_keeps_the_earlier_pending_upload()
    {
        using var directory = new UploadDirectory();
        var registry = new MemoryHouseholdImportUploadRegistry(TimeProvider.System);
        var household = Guid.NewGuid();
        var first = await ReceiveAsync(FormFileOf(MinimalValidDocument), directory, registry, householdId: household);
        var firstToken = first.ShouldBeOfType<Ok<HouseholdImportValidationResponse>>().Value!.Token;
        using var cancel = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReceiveAsync(new CancelAfterCopyFormFile(Encoding.UTF8.GetBytes(MinimalValidDocument), cancel), directory, registry, cancel.Token, householdId: household));

        directory.Files.Length.ShouldBe(1);
        registry.Consume(firstToken, household).ShouldNotBeNull();
    }

    // Code review: invalid UTF-8 is a 400 with failures, never a 500.
    [Fact]
    public async Task A_file_with_invalid_UTF8_answers_400_with_a_failure_and_leaves_no_temp_file()
    {
        using var directory = new UploadDirectory();
        // The member's display name is read as text, which is where Utf8JsonReader notices a bad byte.
        var json = MinimalValidDocument.Replace(
            "\"householdMembers\":[]", "\"householdMembers\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"displayName\":\"d_\"}]");
        json.ShouldContain("d_");
        var bytes = Encoding.UTF8.GetBytes(json).ToList();
        bytes[bytes.IndexOf((byte)'_')] = 0xFC;
        using var stream = new MemoryStream(bytes.ToArray());

        var result = await ReceiveAsync(FormFileOf(stream), directory);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        directory.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_successful_upload_keeps_exactly_its_one_registered_temp_file()
    {
        using var directory = new UploadDirectory();

        var result = await ReceiveAsync(FormFileOf(MinimalValidDocument), directory, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<Ok<HouseholdImportValidationResponse>>();
        directory.Files.Length.ShouldBe(1);
    }

    // The host-level tests below point one API host at its own upload directory, so the file count
    // sees only this test's uploads: other classes upload to the same route in parallel.
    private (WebApplicationFactory<Program> Host, UploadDirectory Directory) HostWithOwnUploadDirectory()
    {
        var directory = new UploadDirectory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new HouseholdImportUploadSettings(directory.Path, HouseholdImportEndpoints.MaxFileSizeBytes))));
        return (host, directory);
    }

    private static async Task<HttpClient> ClientWithHouseholdAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "fetch");
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.IssuerHeader, TestAuthHandler.DefaultIssuer);
        await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);
        return client;
    }

    private static async Task<byte[]> ExportBytesAsync(HttpClient client) =>
        await (await client.GetAsync("/api/household-export", TestContext.Current.CancellationToken))
            .Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

    private static async Task<HouseholdImportValidationResponse> UploadValidAsync(HttpClient client, byte[] bytes)
    {
        using var upload = BuildUpload(bytes);
        var response = await client.PostAsync("/api/household-import", upload, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HouseholdImportValidationResponse>(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Over_HTTP_a_validation_failure_leaves_no_temp_file_and_a_valid_upload_leaves_exactly_one()
    {
        var (host, directory) = HostWithOwnUploadDirectory();
        using var hostScope = host;
        using var directoryScope = directory;
        var client = await ClientWithHouseholdAsync(host);

        using var invalid = BuildUpload(Encoding.UTF8.GetBytes("""{ "formatVersion": "v1" }"""));
        (await client.PostAsync("/api/household-import", invalid, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        directory.Files.ShouldBeEmpty();

        await UploadValidAsync(client, await ExportBytesAsync(client));
        directory.Files.Length.ShouldBe(1);
    }

    // AC #2/#9: a second validated upload by the same Household replaces the first.
    [Fact]
    public async Task A_second_valid_upload_by_the_same_Household_leaves_one_file_and_only_the_second_token_confirms()
    {
        var (host, directory) = HostWithOwnUploadDirectory();
        using var hostScope = host;
        using var directoryScope = directory;
        var client = await ClientWithHouseholdAsync(host);
        var export = await ExportBytesAsync(client);

        var first = await UploadValidAsync(client, export);
        var second = await UploadValidAsync(client, export);

        directory.Files.Length.ShouldBe(1);
        var firstConfirm = await client.PostAsync($"/api/household-import/{first.Token}/confirm", null, TestContext.Current.CancellationToken);
        firstConfirm.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await firstConfirm.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("No pending import");
        var secondConfirm = await client.PostAsync($"/api/household-import/{second.Token}/confirm", null, TestContext.Current.CancellationToken);
        secondConfirm.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = await secondConfirm.Content.ReadFromJsonAsync<HouseholdImportConfirmResponse>(TestContext.Current.CancellationToken);
        (await PollJobToTerminalAsync(client, job!.JobId)).Status.ShouldBe("completed");
    }

    [Fact]
    public async Task Another_Households_pending_upload_is_untouched_by_a_Households_second_upload()
    {
        var (host, directory) = HostWithOwnUploadDirectory();
        using var hostScope = host;
        using var directoryScope = directory;
        var owner = await ClientWithHouseholdAsync(host);
        var other = await ClientWithHouseholdAsync(host);
        var otherUpload = await UploadValidAsync(other, await ExportBytesAsync(other));
        var ownerExport = await ExportBytesAsync(owner);

        await UploadValidAsync(owner, ownerExport);
        await UploadValidAsync(owner, ownerExport);

        directory.Files.Length.ShouldBe(2);
        (await other.PostAsync($"/api/household-import/{otherUpload.Token}/confirm", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // AC #1: a ~33 MB export validates over HTTP and the summary counts are right. The deterministic
    // bounded-buffer proof is the CountingStream test in ValidateHouseholdImportTests.
    [Fact]
    public async Task A_33_MB_upload_returns_200_with_the_right_summary_counts()
    {
        var (host, directory) = HostWithOwnUploadDirectory();
        using var hostScope = host;
        using var directoryScope = directory;
        var client = await ClientWithHouseholdAsync(host);
        var export = JsonNode.Parse(await ExportBytesAsync(client))!.AsObject();
        var powerPointId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        export["rooms"] = new JsonArray(new JsonObject { ["id"] = roomId, ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null });
        export["powerPoints"] = new JsonArray(new JsonObject { ["id"] = powerPointId, ["roomId"] = roomId, ["name"] = "Outlet", ["createdAtUtc"] = "2026-01-05T08:16:00+00:00", ["archivedAt"] = null });
        // Write the large array by hand: ~130,000 readings of ~270 bytes is over 33 MB.
        const int readings = 130_000;
        using var body = new MemoryStream();
        var rootText = export.ToJsonString();
        rootText.ShouldContain("\"smartPlugReadings\":[]");
        var split = rootText.IndexOf("\"smartPlugReadings\":[]", StringComparison.Ordinal);
        body.Write(Encoding.UTF8.GetBytes(rootText[..split] + "\"smartPlugReadings\":["));
        for (var i = 0; i < readings; i++)
        {
            var reading =
                $$"""{"id":"{{Guid.NewGuid()}}","powerPointId":"{{powerPointId}}","roomName":"Kitchen","powerPointName":"Outlet","deviceName":"Kettle","intervalStart":"2026-09-01T00:00:00+00:00","intervalEnd":"2026-09-01T00:10:00+00:00","kwhValue":0.0125}""";
            body.Write(Encoding.UTF8.GetBytes(i == 0 ? reading : "," + reading));
        }

        body.Write(Encoding.UTF8.GetBytes("]" + rootText[(split + "\"smartPlugReadings\":[]".Length)..]));
        body.Length.ShouldBeGreaterThan(33L * 1024 * 1024);

        var result = await UploadValidAsync(client, body.ToArray());

        result.Summary.SmartPlugReadings.ShouldBe(readings);
        result.Summary.Rooms.ShouldBe(1);
        result.Summary.PowerPoints.ShouldBe(1);
        directory.Files.Length.ShouldBe(1);
    }
}
