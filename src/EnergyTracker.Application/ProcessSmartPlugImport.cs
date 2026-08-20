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
    ISmartPlugImportRepository smartPlugImportRepository,
    CompleteSmartPlugImportProcessing completeSmartPlugImportProcessing)
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
                // AC #7 (FR-24): a file that parses to zero rows is entirely gaps — flagged for
                // review, never a hard failure (Story 3.1 built this as a thrown exception before
                // FR-24's softer framing existed). No Power Point was ever resolved and nothing
                // here was used to sharpen anything, so this returns without calling
                // CompleteSmartPlugImportProcessing/IStatusRecomputeService.
                await PersistFlaggedForReviewImportAsync(householdId, backgroundJobId, payload, parser.Vendor, cancellationToken);
                return;
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

            // AD-7/Task 3: gap detection + Status recompute only ever run once a Power Point is
            // resolved (AD-10) — the AwaitingPowerPointMapping branch above is parked without
            // either, exactly as Story 3.2's own by-value snapshot already works for this reason.
            if (matchedPowerPoint is not null)
            {
                await completeSmartPlugImportProcessing.ExecuteAsync(import, readings, cancellationToken);
            }
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

    private async Task PersistFlaggedForReviewImportAsync(
        Guid householdId, Guid backgroundJobId, ProcessSmartPlugImportPayload payload,
        SmartPlugVendorFormat vendor, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var import = new SmartPlugImport
        {
            Id = payload.SmartPlugImportId,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = vendor,
            OriginalFileName = payload.OriginalFileName,
            Status = SmartPlugImportStatus.FlaggedForReview,
            // No rows parsed at all — there's no reading to read a device tag from (unlike the
            // matched/awaiting-mapping branches above).
            DeviceTag = string.Empty,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };

        // Neither vendor format carries a declared date range separate from its own rows (Dev
        // Notes' Open Question #3) — with zero rows parsed, there's no better-effort date to
        // derive one from either, so both bounds fall back to today's date. Flagged as an
        // assumption in Completion Notes, same as Story 2.4/3.2's own "propose a default" pattern.
        var uploadDate = DateOnly.FromDateTime(now.Date);
        var gap = new SmartPlugImportGap
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = null,
            StartDate = uploadDate,
            EndDate = uploadDate,
            Treatment = SmartPlugImportGapTreatment.FlaggedForReview,
            EstimatedTotalKwh = null,
            CreatedAtUtc = now,
        };

        await smartPlugImportRepository.AddFlaggedForReviewAsync(import, gap, cancellationToken);
    }
}

public class SmartPlugImportValidationException(string message) : Exception(message);
