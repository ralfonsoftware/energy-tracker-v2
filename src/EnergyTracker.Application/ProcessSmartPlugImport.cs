using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

// Enqueued via IBackgroundJobQueue with JobType == JobTypes.ProcessSmartPlugImport. Carries only
// a temp-storage reference to the uploaded file, never the raw/base64 bytes — Azure Storage
// Queue caps a message at 64 KB and real Eve Home exports run several hundred KB (Task 3).
public record ProcessSmartPlugImportPayload(Guid SmartPlugImportId, string TempFilePath, string OriginalFileName);

/// <summary>Parses an uploaded Smart Plug export via the matching ISmartPlugParser adapter, attempts a Power Point match by exact name, and persists the SmartPlugImport + its SmartPlugReading rows (AC #3, #4, #5, #6).</summary>
public class ProcessSmartPlugImport(
    IEnumerable<ISmartPlugParser> parsers,
    ITaggingScaffoldRepository taggingScaffoldRepository,
    ISmartPlugImportRepository smartPlugImportRepository)
{
    public async Task ExecuteAsync(
        Guid householdId, Guid backgroundJobId, ProcessSmartPlugImportPayload payload, CancellationToken cancellationToken)
    {
        ISmartPlugParser? parser = null;
        try
        {
            parser = parsers.FirstOrDefault(p => p.CanParse(payload.OriginalFileName))
                ?? throw new SmartPlugImportValidationException($"No parser recognizes file '{payload.OriginalFileName}'.");

            IReadOnlyList<SmartPlugReading> readings;
            await using (var fileStream = File.OpenRead(payload.TempFilePath))
            {
                readings = parser.Parse(fileStream, payload.OriginalFileName, cancellationToken);
            }

            if (readings.Count == 0)
            {
                throw new SmartPlugImportValidationException($"File '{payload.OriginalFileName}' contains no data rows.");
            }

            // Every reading in one file shares the same device tag (Eve Home's "Gerät:" header /
            // Meross's filename segment) — any reading's DeviceName carries it.
            var deviceTag = readings[0].DeviceName;

            var powerPoints = await taggingScaffoldRepository.ListPowerPointsAsync(cancellationToken);
            // Exact-name match only (Task 3) — no fuzzy/case-insensitive matching. A Power Point
            // Name is only unique within its Room (PowerPointConfiguration's (RoomId, Name) index),
            // so the same tag can legitimately hit two Power Points in different Rooms; treat that
            // exactly like zero matches (well-defined AwaitingPowerPointMapping, not an arbitrary
            // pick). Archived Power Points are excluded — a Smart Plug import must never silently
            // resurrect data against a fixture the user can no longer manage through the UI.
            var nameMatches = powerPoints.Where(p => p.ArchivedAt is null && p.Name == deviceTag).ToList();
            var matchedPowerPoint = nameMatches.Count == 1 ? nameMatches[0] : null;

            string? matchedRoomName = null;
            if (matchedPowerPoint is not null)
            {
                var room = await taggingScaffoldRepository.FindRoomAsync(matchedPowerPoint.RoomId, cancellationToken);
                matchedRoomName = room?.Name;
            }

            foreach (var reading in readings)
            {
                reading.HouseholdId = householdId;
                reading.SmartPlugImportId = payload.SmartPlugImportId;

                if (matchedPowerPoint is not null)
                {
                    // AD-10: snapshot the matched Power Point/Room identity by value at write
                    // time — never a live join that a later re-parenting would silently rewrite.
                    reading.PowerPointId = matchedPowerPoint.Id;
                    reading.PowerPointName = matchedPowerPoint.Name;
                    reading.RoomName = matchedRoomName ?? reading.RoomName;
                }
            }

            var import = new SmartPlugImport
            {
                Id = payload.SmartPlugImportId,
                HouseholdId = householdId,
                BackgroundJobId = backgroundJobId,
                VendorFormat = parser.Vendor,
                OriginalFileName = payload.OriginalFileName,
                Status = matchedPowerPoint is not null ? SmartPlugImportStatus.Completed : SmartPlugImportStatus.AwaitingPowerPointMapping,
                DeviceTag = deviceTag,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            };

            await smartPlugImportRepository.AddAsync(import, readings, cancellationToken);

            // AD-7 boundary: Status recompute-on-import-completion is Story 3.3's AC, not this
            // one's — IStatusRecomputeService is deliberately never called from this use case.
        }
        catch (OperationCanceledException)
        {
            // A cancellation (app shutdown/redeploy, not a parse/match failure) must never be
            // recorded as a Failed import — that would permanently discard a job that's actually
            // still retryable once a new instance dequeues it. Leave no SmartPlugImport row and
            // skip the temp-file cleanup below so a redelivered message can still find the file.
            throw;
        }
        catch (Exception)
        {
            // The caller (BackgroundJobProcessor) is the single source of truth for
            // BackgroundJob.Status/ErrorMessage — this only ensures a Failed SmartPlugImport row
            // exists alongside it, then re-throws so that still happens.
            await PersistFailedImportAsync(householdId, backgroundJobId, payload, parser?.Vendor, cancellationToken);
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && File.Exists(payload.TempFilePath))
            {
                File.Delete(payload.TempFilePath);
            }
        }
    }

    private async Task PersistFailedImportAsync(
        Guid householdId, Guid backgroundJobId, ProcessSmartPlugImportPayload payload,
        SmartPlugVendorFormat? resolvedVendor, CancellationToken cancellationToken)
    {
        // Prefer the vendor already resolved via ISmartPlugParser.CanParse (AC #5 — no
        // vendor-specific logic outside the adapter). Only fall back to a filename-extension guess
        // when no parser recognized the file at all, in which case the true vendor is genuinely
        // unknown and this is a best-effort label on an already-Failed row.
        var vendor = resolvedVendor ?? (payload.OriginalFileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? SmartPlugVendorFormat.EveHome
            : SmartPlugVendorFormat.Meross);

        var failedImport = new SmartPlugImport
        {
            Id = payload.SmartPlugImportId,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = vendor,
            OriginalFileName = payload.OriginalFileName,
            Status = SmartPlugImportStatus.Failed,
            DeviceTag = string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        await smartPlugImportRepository.AddAsync(failedImport, [], cancellationToken);
    }
}

public class SmartPlugImportValidationException(string message) : Exception(message);
