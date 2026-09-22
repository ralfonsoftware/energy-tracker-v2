using EnergyTracker.Application.Ports;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class RestoreHouseholdDataTests
{
    private readonly IHouseholdRestoreWriter _writer = Substitute.For<IHouseholdRestoreWriter>();

    private RestoreHouseholdData Sut() => new(_writer);

    private static string WriteTempFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string ValidJson(Guid householdId) =>
        $$"""
        {
          "formatVersion": "v2",
          "exportedAtUtc": "2026-09-22T10:00:00+00:00",
          "household": {
            "id": "{{householdId}}",
            "createdAtUtc": "2026-01-05T08:00:00+00:00",
            "locale": "de-DE",
            "currency": "EUR",
            "yearlyBaselineKwh": 3500.0,
            "trendingThresholdKwh": 100.0,
            "lowConfidenceGapDays": 45,
            "tariffCheckCadenceMonths": 3,
            "aiPlausibilityEnabled": true,
            "aiPlausibilityBackendConfigured": true,
            "aiPlausibilityBackendLabel": "Local (LMStudio)"
          },
          "householdMembers": [],
          "mainMeter": null,
          "meterReadings": [],
          "meterRegressionPrompts": [],
          "tariffs": [],
          "events": [],
          "rooms": [],
          "powerPoints": [],
          "devices": [],
          "smartPlugReadings": [],
          "statusSnapshots": [],
          "auditCorrections": []
        }
        """;

    [Fact]
    public async Task Passes_the_current_Household_id_never_the_files_own_household_id()
    {
        var currentHouseholdId = Guid.NewGuid();
        var fileHouseholdId = Guid.NewGuid();
        var tempFilePath = WriteTempFile(ValidJson(fileHouseholdId));
        var payload = new RestoreHouseholdDataPayload(tempFilePath, "export.json");

        await Sut().ExecuteAsync(currentHouseholdId, payload, TestContext.Current.CancellationToken);

        await _writer.Received(1).RestoreAsync(
            Arg.Is<HouseholdRestoreData>(d => d.HouseholdId == currentHouseholdId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sets_HouseholdId_explicitly_on_every_reconstructed_entity()
    {
        var currentHouseholdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var powerPointId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var mainMeterReadingId = Guid.NewGuid();
        var json =
            $$"""
            {
              "formatVersion": "v2",
              "exportedAtUtc": "2026-09-22T10:00:00+00:00",
              "household": {
                "id": "{{Guid.NewGuid()}}", "createdAtUtc": "2026-01-05T08:00:00+00:00", "locale": "de-DE", "currency": "EUR",
                "yearlyBaselineKwh": null, "trendingThresholdKwh": 100.0, "lowConfidenceGapDays": 45, "tariffCheckCadenceMonths": 3,
                "aiPlausibilityEnabled": false, "aiPlausibilityBackendConfigured": false, "aiPlausibilityBackendLabel": null
              },
              "householdMembers": [{
                "id": "{{Guid.NewGuid()}}", "externalIssuer": "https://issuer.test/", "externalSubjectId": "sub-1",
                "displayName": "Ralf", "createdAtUtc": "2026-01-05T08:00:00+00:00"
              }],
              "mainMeter": { "id": "{{mainMeterId}}", "createdAtUtc": "2026-01-05T09:00:00+00:00", "digitCapacityKwh": null },
              "meterReadings": [{
                "id": "{{mainMeterReadingId}}", "mainMeterId": "{{mainMeterId}}", "kwhValue": 100.0,
                "readingTimestamp": "2026-09-01T00:00:00+00:00", "idempotencyKey": "{{Guid.NewGuid()}}",
                "createdAtUtc": "2026-09-01T00:00:05+00:00"
              }],
              "meterRegressionPrompts": [],
              "tariffs": [{
                "id": "{{Guid.NewGuid()}}", "monthlyBaseFee": 8.5, "pricePerKwh": 0.32, "currency": "EUR",
                "contractStartDate": "2026-01-01T00:00:00+00:00", "contractPeriodMonths": 12, "createdAtUtc": "2026-01-05T08:30:00+00:00"
              }],
              "events": [{
                "id": "{{Guid.NewGuid()}}", "description": "cooked 2h", "occurredAt": "2026-09-01T00:00:00+00:00",
                "createdAtUtc": "2026-09-01T00:00:00+00:00", "taggedEntityType": null, "taggedEntityId": null,
                "taggedEntityName": null, "correlationDirection": null, "correlationComputedAtUtc": null
              }],
              "rooms": [{ "id": "{{roomId}}", "name": "Kitchen", "createdAtUtc": "2026-01-05T08:15:00+00:00", "archivedAt": null }],
              "powerPoints": [{
                "id": "{{powerPointId}}", "roomId": "{{roomId}}", "name": "Outlet", "createdAtUtc": "2026-01-05T08:16:00+00:00",
                "archivedAt": null
              }],
              "devices": [{
                "id": "{{deviceId}}", "powerPointId": "{{powerPointId}}", "name": "Kettle",
                "createdAtUtc": "2026-01-05T08:17:00+00:00", "archivedAt": null
              }],
              "smartPlugReadings": [{
                "id": "{{Guid.NewGuid()}}", "powerPointId": "{{powerPointId}}", "roomName": "Kitchen", "powerPointName": "Outlet",
                "deviceName": "Kettle", "intervalStart": "2026-09-01T00:00:00+00:00", "intervalEnd": "2026-09-01T00:15:00+00:00",
                "kwhValue": 0.5
              }],
              "statusSnapshots": [{
                "id": "{{Guid.NewGuid()}}", "status": "withinrange", "paceToDateKwh": 10.0, "baselineToDateKwh": 12.0,
                "isLowConfidence": false, "computedAtUtc": "2026-09-01T00:00:00+00:00"
              }],
              "auditCorrections": [{
                "id": "{{Guid.NewGuid()}}", "entityType": "MeterReading", "entityId": "{{mainMeterReadingId}}", "fieldName": "KwhValue",
                "oldValue": "90", "newValue": "100", "correctedAtUtc": "2026-09-01T00:00:00+00:00"
              }]
            }
            """;
        var tempFilePath = WriteTempFile(json);
        HouseholdRestoreData? captured = null;
        _writer.RestoreAsync(Arg.Do<HouseholdRestoreData>(d => captured = d), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await Sut().ExecuteAsync(currentHouseholdId, new RestoreHouseholdDataPayload(tempFilePath, "export.json"), TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.MainMeter!.HouseholdId.ShouldBe(currentHouseholdId);
        captured.HouseholdMembers.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.MeterReadings.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.Tariffs.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.Events.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.Rooms.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.PowerPoints.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.Devices.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.SmartPlugReadings.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.StatusSnapshots.Single().HouseholdId.ShouldBe(currentHouseholdId);
        captured.AuditCorrections.Single().HouseholdId.ShouldBe(currentHouseholdId);
        // Internal cross-references reused byte-for-byte from the file — never remapped.
        captured.PowerPoints.Single().RoomId.ShouldBe(roomId);
        captured.Devices.Single().PowerPointId.ShouldBe(powerPointId);
        captured.SmartPlugReadings.Single().PowerPointId.ShouldBe(powerPointId);
        // SmartPlugImportId is always null on insert — the field isn't in the export at all.
        captured.SmartPlugReadings.Single().SmartPlugImportId.ShouldBeNull();
    }

    [Fact]
    public async Task Deletes_the_temp_file_after_a_successful_restore()
    {
        var tempFilePath = WriteTempFile(ValidJson(Guid.NewGuid()));

        await Sut().ExecuteAsync(Guid.NewGuid(), new RestoreHouseholdDataPayload(tempFilePath, "export.json"), TestContext.Current.CancellationToken);

        File.Exists(tempFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Deletes_the_temp_file_even_when_the_writer_throws()
    {
        var tempFilePath = WriteTempFile(ValidJson(Guid.NewGuid()));
        _writer.RestoreAsync(Arg.Any<HouseholdRestoreData>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => Sut().ExecuteAsync(Guid.NewGuid(), new RestoreHouseholdDataPayload(tempFilePath, "export.json"), TestContext.Current.CancellationToken));

        File.Exists(tempFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_temp_file_throws_a_HouseholdImportValidationException_with_a_user_facing_message()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");

        var ex = await Should.ThrowAsync<HouseholdImportValidationException>(
            () => Sut().ExecuteAsync(Guid.NewGuid(), new RestoreHouseholdDataPayload(missingPath, "export.json"), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("export.json");
        await _writer.DidNotReceive().RestoreAsync(Arg.Any<HouseholdRestoreData>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Never_calls_IAuditCorrectionRecorder_this_path_has_no_such_dependency_at_all()
    {
        // AD-11's explicit carve-out — proven structurally here: RestoreHouseholdData's only
        // dependency is IHouseholdRestoreWriter, so there is no IAuditCorrectionRecorder reference
        // anywhere in this class to call in the first place.
        typeof(RestoreHouseholdData).GetConstructors().Single().GetParameters()
            .ShouldNotContain(p => p.ParameterType.Name.Contains("AuditCorrection"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Maps_MeterRegressionPrompt_classification_and_StatusSnapshot_status_back_to_the_real_enum()
    {
        var currentHouseholdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var previousReadingId = Guid.NewGuid();
        var json =
            $$"""
            {
              "formatVersion": "v2",
              "exportedAtUtc": "2026-09-22T10:00:00+00:00",
              "household": {
                "id": "{{Guid.NewGuid()}}", "createdAtUtc": "2026-01-05T08:00:00+00:00", "locale": "de-DE", "currency": "EUR",
                "yearlyBaselineKwh": null, "trendingThresholdKwh": 100.0, "lowConfidenceGapDays": 45, "tariffCheckCadenceMonths": 3,
                "aiPlausibilityEnabled": false, "aiPlausibilityBackendConfigured": false, "aiPlausibilityBackendLabel": null
              },
              "householdMembers": [], "mainMeter": null, "meterReadings": [],
              "meterRegressionPrompts": [{
                "id": "{{Guid.NewGuid()}}", "mainMeterId": "{{mainMeterId}}", "meterReadingId": "{{readingId}}",
                "previousMeterReadingId": "{{previousReadingId}}", "createdAtUtc": "2026-01-05T08:00:00+00:00",
                "resolvedAtUtc": null, "classification": "rollover", "digitCapacityKwh": 999.9
              }],
              "tariffs": [], "events": [], "rooms": [], "powerPoints": [], "devices": [], "smartPlugReadings": [],
              "statusSnapshots": [{
                "id": "{{Guid.NewGuid()}}", "status": "trending", "paceToDateKwh": 10.0, "baselineToDateKwh": 8.0,
                "isLowConfidence": false, "computedAtUtc": "2026-09-01T00:00:00+00:00"
              }],
              "auditCorrections": []
            }
            """;
        var tempFilePath = WriteTempFile(json);
        HouseholdRestoreData? captured = null;
        _writer.RestoreAsync(Arg.Do<HouseholdRestoreData>(d => captured = d), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await Sut().ExecuteAsync(currentHouseholdId, new RestoreHouseholdDataPayload(tempFilePath, "export.json"), TestContext.Current.CancellationToken);

        captured!.MeterRegressionPrompts.Single().Classification.ShouldBe(Domain.MeterRegressionClassification.Rollover);
        captured.StatusSnapshots.Single().Status.ShouldBe(Domain.Status.Trending);
    }
}
