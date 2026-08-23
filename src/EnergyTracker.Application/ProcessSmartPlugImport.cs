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

            // Story 3.4 AC #1: the Power Point match (and its watermark) is resolved from the
            // file's header before the data body is read at all — never a full parse first.
            string deviceTag;
            await using (var headerStream = File.OpenRead(payload.TempFilePath))
            {
                deviceTag = parser.ReadDeviceTag(headerStream, payload.OriginalFileName, cancellationToken);
            }

            var powerPoints = await taggingScaffoldRepository.ListPowerPointsAsync(cancellationToken);
            // Exact-name match only (Task 3) — no fuzzy/case-insensitive matching. A Power Point
            // Name is only unique within its Room (PowerPointConfiguration's (RoomId, Name) index),
            // so the same tag can legitimately hit two Power Points in different Rooms; treat that
            // exactly like zero matches (well-defined AwaitingPowerPointMapping, not an arbitrary
            // pick). Archived Power Points are excluded — a Smart Plug import must never silently
            // resurrect data against a fixture the user can no longer manage through the UI.
            var nameMatches = powerPoints.Where(p => p.ArchivedAt is null && p.Name == deviceTag).ToList();
            var matchedPowerPoint = nameMatches.Count == 1 ? nameMatches[0] : null;

            // AC #4: null whenever there's no Power Point match at all (AwaitingPowerPointMapping)
            // or the matched Power Point has no prior stored reading yet (first-ever import) —
            // either way the parser parses the file in full, exactly as before this story.
            string? matchedRoomName = null;
            DateTimeOffset? watermark = null;
            if (matchedPowerPoint is not null)
            {
                var room = await taggingScaffoldRepository.FindRoomAsync(matchedPowerPoint.RoomId, cancellationToken);
                matchedRoomName = room?.Name;
                watermark = await smartPlugImportRepository.FindLatestReadingIntervalStartByPowerPointAsync(matchedPowerPoint.Id, cancellationToken);
            }

            SmartPlugParseResult parseResult;
            await using (var dataStream = File.OpenRead(payload.TempFilePath))
            {
                parseResult = parser.Parse(dataStream, payload.OriginalFileName, watermark, cancellationToken);
            }

            var readings = parseResult.Readings;

            if (readings.Count == 0)
            {
                if (watermark is null || parseResult.RawDataRowsRead == 0)
                {
                    // AC #7 (FR-24): the file body itself had zero data rows to read at all —
                    // either genuinely empty/all-header (watermark is null: no Power Point was
                    // ever resolved) or, review-round-2 patch, a matched Power Point's re-upload
                    // whose body still turned out to have nothing in it (a corrupt/truncated file,
                    // not a legitimate "nothing new"). Flagged for review either way, never a hard
                    // failure (Story 3.1 built this as a thrown exception before FR-24's softer
                    // framing existed) and never silently marked Completed.
                    await PersistFlaggedForReviewImportAsync(householdId, backgroundJobId, payload, parser.Vendor, deviceTag, cancellationToken);
                    return;
                }

                // Story 3.4: a normal, successful "nothing new" incremental re-import — rows were
                // read (RawDataRowsRead > 0) but every one was at-or-before the watermark.
                // Distinct from AC #7 above: this must NOT be flagged for review. Persist a
                // Completed import with zero readings (AddAsync already handles readings.Count
                // == 0 correctly) and skip CompleteSmartPlugImportProcessing — it requires a
                // non-empty readings list (Story 3.3), and there is nothing new here to run gap
                // detection/Status recompute against.
                await PersistCompletedEmptyImportAsync(householdId, backgroundJobId, payload, parser.Vendor, deviceTag, cancellationToken);
                return;
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

    private async Task PersistCompletedEmptyImportAsync(
        Guid householdId, Guid backgroundJobId, ProcessSmartPlugImportPayload payload,
        SmartPlugVendorFormat vendor, string deviceTag, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var import = new SmartPlugImport
        {
            Id = payload.SmartPlugImportId,
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = vendor,
            OriginalFileName = payload.OriginalFileName,
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = deviceTag,
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };

        await smartPlugImportRepository.AddAsync(import, [], cancellationToken);
    }

    private async Task PersistFlaggedForReviewImportAsync(
        Guid householdId, Guid backgroundJobId, ProcessSmartPlugImportPayload payload,
        SmartPlugVendorFormat vendor, string deviceTag, CancellationToken cancellationToken)
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
            // No rows parsed at all, but the header (Task 1's ReadDeviceTag) was already read
            // before the body — this is known even though nothing here was used to sharpen a
            // Power Point match.
            DeviceTag = deviceTag,
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
