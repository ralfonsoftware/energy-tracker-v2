using System.Net;
using System.Net.Http.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
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
        return BuildUploadFromBytes(bytes, Path.GetFileName(filePath));
    }

    private static MultipartFormDataContent BuildUploadFromBytes(byte[] bytes, string fileName, string contentType = "text/csv")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return content;
    }

    // Meross's real byte layout (Story 3.1): UTF-8, "\t," field delimiter, trailing tab per line —
    // built directly here rather than via a fixture file, so each test can control the exact
    // date/value shape a Smart Plug import gap needs.
    private static byte[] BuildMerossCsv(IEnumerable<(DateOnly Date, decimal Kwh)> rows)
    {
        var lines = new List<string> { "Date\t,Power Consumption-(kWh)\t" };
        lines.AddRange(rows.Select(r => $"{r.Date:yyyy-MM-dd}\t,{r.Kwh.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t"));
        return System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
    }

    // Builds a minimal but structurally valid Eve Home-layout workbook in memory, newest-first,
    // one row per entry in `timestamps` — lets a test control the exact date shape a Story 3.4
    // incremental-reimport scenario needs, the same way BuildMerossCsv already does for Meross,
    // since the committed EveSampleFilePath fixture's content is fixed.
    private static byte[] BuildEveHomeWorkbook(string deviceName, string roomName, IReadOnlyList<DateTime> timestamps)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: false))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Gesamtverbrauch",
            });

            uint rowIndex = 1;
            sheetData.Append(BuildRow(rowIndex++, $"Gerät: {deviceName}"));
            sheetData.Append(BuildRow(rowIndex++, $"Raum: {roomName}"));
            sheetData.Append(BuildRow(rowIndex++, "Zuhause: Test-Zuhause"));
            sheetData.Append(BuildRow(rowIndex++, "Zeitstempel", "Wh"));

            foreach (var timestamp in timestamps)
            {
                sheetData.Append(BuildRow(rowIndex++, timestamp.ToString("yyyy-MM-dd HH:mm:ss"), "820"));
            }

            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();

        static Row BuildRow(uint index, params string[] cellTexts)
        {
            var row = new Row { RowIndex = index };
            foreach (var text in cellTexts)
            {
                row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(text) });
            }

            return row;
        }
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

    private static Task<HttpResponseMessage> SetYearlyBaselineAsync(HttpClient client, Guid householdId, decimal yearlyBaselineKwh, int version) =>
        client.PutAsJsonAsync(
            $"/api/households/{householdId}/yearly-baseline", new { yearlyBaselineKwh, version }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> PostReadingAsync(HttpClient client, decimal kwhValue, DateTimeOffset readingTimestamp) =>
        client.PostAsJsonAsync(
            "/api/meter-readings",
            new { kwhValue, readingTimestamp, idempotencyKey = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

    // AD-3's query filter needs an HttpContext-bound CurrentHouseholdAccessor, absent in this raw
    // scope — IgnoreQueryFilters is required to count/read directly against the table.
    private async Task<int> CountStatusSnapshotRowsAsync(Guid householdId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.StatusSnapshots.IgnoreQueryFilters()
            .CountAsync(s => s.HouseholdId == householdId, TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task An_import_with_a_mid_file_gap_surfaces_it_in_the_jobs_Gaps_field()
    {
        var (client, _) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/api/power-points", new { roomId = room!.Id, name = "Verbraucher 1" }, TestContext.Current.CancellationToken);

        // 7 preceding days of real data (a genuine full week), then a 2-day gap, then 1 more day —
        // matches SmartPlugGapDetectorTests' "sufficient preceding data" shape.
        var start = new DateOnly(2026, 6, 1);
        var rows = Enumerable.Range(0, 7).Select(i => (start.AddDays(i), 4m)).ToList();
        rows.Add((start.AddDays(9), 4m));
        using var upload = BuildUploadFromBytes(BuildMerossCsv(rows), "Power Monitor Day Data - Verbraucher 1 - 20260601.csv");

        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, body!.JobId);

        terminalStatus.ImportStatus.ShouldBe("completed");
        terminalStatus.Gaps.Count.ShouldBe(1);
        terminalStatus.Gaps[0].Treatment.ShouldBe("estimated");
        terminalStatus.Gaps[0].StartDate.ShouldBe(start.AddDays(7));
        terminalStatus.Gaps[0].EndDate.ShouldBe(start.AddDays(8));
        terminalStatus.Gaps[0].EstimatedTotalKwh.ShouldNotBeNull();
    }

    [Fact]
    public async Task Mapping_an_unmatched_import_also_runs_gap_detection_and_a_Status_recompute()
    {
        // This is the concrete regression test for Task 3's "two completion paths" requirement —
        // MapSmartPlugImportToPowerPoint must trigger the exact same AD-7 wiring
        // ProcessSmartPlugImport's direct-match branch already does.
        var (client, householdId) = await CreateHouseholdAsync();
        await SetYearlyBaselineAsync(client, householdId, 3650m, version: 0);
        await PostReadingAsync(client, 1000m, DateTimeOffset.UtcNow.AddDays(-10));
        await PostReadingAsync(client, 1100m, DateTimeOffset.UtcNow);
        var snapshotCountBeforeMapping = await CountStatusSnapshotRowsAsync(householdId);

        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "A different outlet" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        // A genuine mid-file gap (not just two contiguous days) — this is the concrete regression
        // test for gap-detection wiring on the mapping path specifically, not only Status recompute.
        var start = new DateOnly(2026, 6, 1);
        var rows = new List<(DateOnly, decimal)> { (start, 2m), (start.AddDays(2), 2m) };
        using var upload = BuildUploadFromBytes(BuildMerossCsv(rows), "Power Monitor Day Data - Unmatched Tag - 20260601.csv");
        var uploadResponse = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var awaitingStatus = await PollJobToTerminalAsync(client, uploadBody!.JobId);
        awaitingStatus.ImportStatus.ShouldBe("awaitingpowerpointmapping");

        var mappingResponse = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{awaitingStatus.SmartPlugImportId}/power-point-mapping",
            new { powerPointId = powerPoint!.Id },
            TestContext.Current.CancellationToken);
        mappingResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var finalStatus = await client.GetFromJsonAsync<JobStatusResponse>($"/api/jobs/{uploadBody.JobId}", TestContext.Current.CancellationToken);
        finalStatus!.ImportStatus.ShouldBe("completed");
        finalStatus.Gaps.Count.ShouldBe(1);
        finalStatus.Gaps[0].StartDate.ShouldBe(start.AddDays(1));
        finalStatus.Gaps[0].EndDate.ShouldBe(start.AddDays(1));

        var snapshotCountAfterMapping = await CountStatusSnapshotRowsAsync(householdId);
        snapshotCountAfterMapping.ShouldBeGreaterThan(snapshotCountBeforeMapping);
    }

    [Fact]
    public async Task An_import_that_parses_to_zero_rows_is_flagged_for_review_with_no_Status_recompute()
    {
        var (client, householdId) = await CreateHouseholdAsync();
        var headerOnlyCsv = BuildMerossCsv([]);
        using var upload = BuildUploadFromBytes(headerOnlyCsv, "Power Monitor Day Data - Empty File - 20260601.csv");

        var response = await client.PostAsync("/api/smart-plug-imports", upload, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);

        var terminalStatus = await PollJobToTerminalAsync(client, body!.JobId);

        terminalStatus.ImportStatus.ShouldBe("flaggedforreview");
        terminalStatus.Gaps.Count.ShouldBe(1);
        terminalStatus.Gaps[0].Treatment.ShouldBe("flaggedforreview");
        (await CountStatusSnapshotRowsAsync(householdId)).ShouldBe(0);
    }

    [Fact]
    public async Task Reimporting_an_overlapping_superset_file_only_persists_the_genuinely_new_rows()
    {
        // Story 3.4 end-to-end regression: upload a file, then re-upload an overlapping/superset
        // file for the same (already-matched) Power Point — only the rows newer than the
        // Power Point's stored watermark must be persisted by the second import, not a duplicate
        // of everything the first import already wrote.
        var (client, householdId) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Verbraucher 1" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        var start = new DateOnly(2026, 6, 1);
        var firstRows = Enumerable.Range(0, 5).Select(i => (start.AddDays(i), 4m)).ToList();
        using var firstUpload = BuildUploadFromBytes(BuildMerossCsv(firstRows), "Power Monitor Day Data - Verbraucher 1 - 20260601.csv");
        var firstResponse = await client.PostAsync("/api/smart-plug-imports", firstUpload, TestContext.Current.CancellationToken);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var firstStatus = await PollJobToTerminalAsync(client, firstBody!.JobId);
        firstStatus.ImportStatus.ShouldBe("completed");

        // Superset: the same 5 already-imported days plus 2 genuinely new ones.
        var secondRows = Enumerable.Range(0, 7).Select(i => (start.AddDays(i), 4m)).ToList();
        using var secondUpload = BuildUploadFromBytes(BuildMerossCsv(secondRows), "Power Monitor Day Data - Verbraucher 1 - 20260608.csv");
        var secondResponse = await client.PostAsync("/api/smart-plug-imports", secondUpload, TestContext.Current.CancellationToken);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var secondStatus = await PollJobToTerminalAsync(client, secondBody!.JobId);
        secondStatus.ImportStatus.ShouldBe("completed");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var readings = await dbContext.SmartPlugReadings.IgnoreQueryFilters()
            .Where(r => r.HouseholdId == householdId && r.PowerPointId == powerPoint!.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // 7 distinct days total, never 12 (5 from the first import + all 7 re-persisted by the
        // second) — the watermark filtered out the 5 already-stored days on the second upload.
        readings.Count.ShouldBe(7);
        readings.Select(r => r.IntervalStart).Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public async Task Reimporting_an_overlapping_superset_Eve_Home_file_only_persists_the_genuinely_new_rows()
    {
        // Story 3.4 end-to-end regression, Eve Home variant of the Meross test above — Eve Home's
        // streaming/early-stop parse (AC #2) is the highest-risk part of this change and had no
        // dedicated end-to-end coverage. Newest-first, matching the vendor's real row order.
        var (client, householdId) = await CreateHouseholdAsync();
        var roomResponse = await client.PostAsJsonAsync("/api/rooms", new { name = "Living room" }, TestContext.Current.CancellationToken);
        var room = await roomResponse.Content.ReadFromJsonAsync<RoomResponse>(TestContext.Current.CancellationToken);
        var powerPointResponse = await client.PostAsJsonAsync(
            "/api/power-points", new { roomId = room!.Id, name = "Steckdose 1" }, TestContext.Current.CancellationToken);
        var powerPoint = await powerPointResponse.Content.ReadFromJsonAsync<PowerPointResponse>(TestContext.Current.CancellationToken);

        var start = new DateTime(2026, 6, 1, 12, 0, 0);
        var firstTimestamps = Enumerable.Range(0, 5).Select(i => start.AddDays(4 - i)).ToList();
        using var firstUpload = BuildUploadFromBytes(
            BuildEveHomeWorkbook("Steckdose 1", "Living room", firstTimestamps), "first.xlsx", "application/octet-stream");
        var firstResponse = await client.PostAsync("/api/smart-plug-imports", firstUpload, TestContext.Current.CancellationToken);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var firstStatus = await PollJobToTerminalAsync(client, firstBody!.JobId);
        firstStatus.ImportStatus.ShouldBe("completed");

        // Superset, newest-first: the same 5 already-imported days plus 2 genuinely new ones.
        var secondTimestamps = Enumerable.Range(0, 7).Select(i => start.AddDays(6 - i)).ToList();
        using var secondUpload = BuildUploadFromBytes(
            BuildEveHomeWorkbook("Steckdose 1", "Living room", secondTimestamps), "second.xlsx", "application/octet-stream");
        var secondResponse = await client.PostAsync("/api/smart-plug-imports", secondUpload, TestContext.Current.CancellationToken);
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<SmartPlugImportUploadResponse>(TestContext.Current.CancellationToken);
        var secondStatus = await PollJobToTerminalAsync(client, secondBody!.JobId);
        secondStatus.ImportStatus.ShouldBe("completed");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        var readings = await dbContext.SmartPlugReadings.IgnoreQueryFilters()
            .Where(r => r.HouseholdId == householdId && r.PowerPointId == powerPoint!.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // 7 distinct rows total, never 12 (5 from the first import + all 7 re-persisted by the
        // second) — the watermark filtered out the 5 already-stored rows on the second upload.
        readings.Count.ShouldBe(7);
        readings.Select(r => r.IntervalStart).Distinct().Count().ShouldBe(7);
    }
}
