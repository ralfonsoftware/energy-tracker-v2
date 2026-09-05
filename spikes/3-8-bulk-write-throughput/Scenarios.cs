using System.Diagnostics;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BulkWriteThroughputSpike;

public static class Scenarios
{
    // Synthetic, fixed household/import identity — never a real one.
    public static readonly Guid HouseholdId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    // Fixed, not DateTimeOffset.UtcNow: batches must regenerate to byte-identical IntervalStart
    // values whether called within one `run-all` process or across separate `ac5`/`ac6` process
    // invocations (the DTU-caution, scenario-by-scenario path — see README.md).
    public static readonly DateTimeOffset AnchorEnd = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    // SPIKE FINDING (empirically verified against real Postgres, see README.md "Findings log"):
    // AD-23's text says "PropertiesToExclude = [Id] on every call". Tried literally, this fails —
    // EFCore.BulkExtensions.PostgreSql 10.0.1's blanket PropertiesToExclude omits the column from
    // the INSERT list too (confirmed via the package's own shipped XML doc comment: "When doing
    // Insert/Update one or more properties can be excluded... PropertiesToExcludeOnUpdate... can
    // differ from PropertiesToExclude that can be used for Insert config only"), which throws a
    // NOT NULL violation on Id for a genuinely-new row — SmartPlugReading.Id is a client-
    // generated Guid (Guid.NewGuid(), no DB-side DEFAULT/IDENTITY exists in either provider's
    // migrations). AD-23's actual intent — never let an UPDATE overwrite a matched row's own Id
    // with the incoming row's Id — is what PropertiesToExcludeOnUpdate expresses; INSERT must
    // still carry the client-generated Id. Story 3.9 should use PropertiesToExcludeOnUpdate, not
    // the blanket PropertiesToExclude, for this reason.
    private static BulkConfig BaseConfig() => new()
    {
        PropertiesToExcludeOnUpdate = ["Id"],
        // SPIKE FINDING: EFCore.BulkExtensions defaults BulkCopyTimeout to SqlBulkCopy's own
        // default (30 seconds per the package's shipped XML doc comment) — Azure SQL Basic tier
        // (5 DTU) did not complete a 120,000-row BulkInsertOrUpdateAsync within that default when
        // run against the real instance ("Execution Timeout Expired"). 0 means no limit per the
        // same doc comment — this story needs an actual measured elapsed time, not an arbitrary
        // cutoff, so there is no safe non-zero value to pick here instead.
        BulkCopyTimeout = 0,
    };

    public static async Task CreateSchemaAsync(SpikeDbContext db, SpikeProvider provider)
    {
        foreach (var sql in SchemaSql.CreateStatements(provider))
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        Console.WriteLine("Schema created.");
    }

    public static async Task DropSchemaAsync(SpikeDbContext db, SpikeProvider provider)
    {
        foreach (var sql in SchemaSql.DropStatements(provider))
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        Console.WriteLine("Schema dropped.");
    }

    public static async Task AssertNoSpikeObjectsAsync(SpikeDbContext db, SpikeProvider provider)
    {
        var remaining = await db.Database
            .SqlQueryRaw<string>(SchemaSql.RemainingSpikeObjectsQuery(provider))
            .ToListAsync();

        Console.WriteLine(remaining.Count == 0
            ? "AC #10 PASS: zero Spike_* objects remain."
            : $"AC #10 FAIL: {remaining.Count} Spike_* object(s) still present: {string.Join(", ", remaining)}");
    }

    public static async Task TruncateReadingsAsync(SpikeDbContext db, SpikeProvider provider)
    {
        await db.Database.ExecuteSqlRawAsync(SchemaSql.TruncateReadingsStatement(provider));
        Console.WriteLine("Spike_SmartPlugReading truncated.");
    }

    private static async Task<Guid> InsertParentRowAsync(SpikeDbContext db)
    {
        var import = new SpikeSmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = HouseholdId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.SpikeSmartPlugImports.Add(import);
        await db.SaveChangesAsync();
        return import.Id;
    }

    // AC #4: BulkInsertOrUpdateAsync into an empty (index-only) table.
    public static async Task<double> Ac4InsertEmptyAsync(SpikeDbContext db, string providerName)
    {
        var existing = await db.SpikeSmartPlugReadings.CountAsync();
        if (existing != 0)
        {
            Console.WriteLine($"WARNING: expected an empty table for AC #4, found {existing} rows. Run 'setup' or 'truncate' first.");
        }

        var importId = await InsertParentRowAsync(db);
        var batch = DataGenerator.GenerateSingleDeviceBatch(
            HouseholdId, powerPointId: Guid.NewGuid(), importId, AnchorEnd).ToList();

        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(batch, BaseConfig());
        sw.Stop();

        ResultsLog.Record(providerName, "AC4-insert-empty-120k", batch.Count, sw.Elapsed.TotalMilliseconds);
        return sw.Elapsed.TotalMilliseconds;
    }

    // AC #5: pre-load ~470k baseline, then insert a further ~120k batch for a new, non-colliding
    // PowerPointId — the index-maintenance-under-load scenario.
    public static async Task<List<Guid>> PreloadAsync(SpikeDbContext db, string providerName)
    {
        var powerPointIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var importId = await InsertParentRowAsync(db);
        var batch = DataGenerator.GeneratePreloadBatch(
            HouseholdId, powerPointIds, importId, AnchorEnd).ToList();

        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(batch, BaseConfig());
        sw.Stop();

        ResultsLog.Record(providerName, "preload-470k-baseline (not itself an AC scenario)", batch.Count, sw.Elapsed.TotalMilliseconds);
        return powerPointIds;
    }

    public static async Task<(Guid PowerPointId, List<SpikeSmartPlugReading> Batch)> Ac5InsertIntoPreloadedAsync(
        SpikeDbContext db, string providerName)
    {
        var newPowerPointId = Guid.NewGuid();
        var importId = await InsertParentRowAsync(db);
        var batch = DataGenerator.GenerateSingleDeviceBatch(
            HouseholdId, newPowerPointId, importId, AnchorEnd).ToList();

        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(batch, BaseConfig());
        sw.Stop();

        ResultsLog.Record(providerName, "AC5-insert-120k-into-470k-preloaded", batch.Count, sw.Elapsed.TotalMilliseconds);
        return (newPowerPointId, batch);
    }

    // AC #6a: 100%-conflicting resubmission of an already-inserted batch, matched on
    // (PowerPointId, IntervalStart) — the full-history-re-import worst case.
    public static async Task Ac6aResubmitFullOverlapAsync(
        SpikeDbContext db, string providerName, List<SpikeSmartPlugReading> previouslyInsertedBatch)
    {
        // Same rows, same key values — only KwhValue perturbed slightly so this is a genuine
        // update, not a no-op the provider could short-circuit.
        var resubmission = previouslyInsertedBatch
            .Select(r => new SpikeSmartPlugReading
            {
                Id = Guid.NewGuid(), // excluded via PropertiesToExclude=[Id]; irrelevant to the match
                HouseholdId = r.HouseholdId,
                PowerPointId = r.PowerPointId,
                IntervalStart = r.IntervalStart,
                IntervalEnd = r.IntervalEnd,
                KwhValue = r.KwhValue + 0.000001m,
                RoomName = r.RoomName,
                PowerPointName = r.PowerPointName,
                DeviceName = r.DeviceName,
                SmartPlugImportId = r.SmartPlugImportId,
            })
            .ToList();

        var config = BaseConfig();
        config.UpdateByProperties = [nameof(SpikeSmartPlugReading.PowerPointId), nameof(SpikeSmartPlugReading.IntervalStart)];

        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(resubmission, config);
        sw.Stop();

        ResultsLog.Record(providerName, "AC6a-resubmit-full-overlap-120k", resubmission.Count, sw.Elapsed.TotalMilliseconds);
    }

    // AC #6b: ~500-row typical-incremental-delta batch, partially overlapping stored rows.
    public static async Task Ac6bResubmitIncrementalDeltaAsync(
        SpikeDbContext db, string providerName, Guid powerPointId, IReadOnlyList<DateTimeOffset> existingIntervalStarts)
    {
        var importId = await InsertParentRowAsync(db);
        var batch = DataGenerator.GenerateIncrementalDeltaBatch(
            HouseholdId, powerPointId, importId,
            overlapExisting: existingIntervalStarts,
            newRowsStart: existingIntervalStarts.Max() + TimeSpan.FromMinutes(10)).ToList();

        var config = BaseConfig();
        config.UpdateByProperties = [nameof(SpikeSmartPlugReading.PowerPointId), nameof(SpikeSmartPlugReading.IntervalStart)];

        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(batch, config);
        sw.Stop();

        ResultsLog.Record(providerName, "AC6b-resubmit-incremental-delta-500", batch.Count, sw.Elapsed.TotalMilliseconds);
    }

    // AC #7: ~5,000-row PowerPointId IS NULL batch, pure insert then full resubmission via
    // UpdateByProperties=[HouseholdId, IntervalStart] — plus the one empirical check this spike
    // exists to nail down: does that match key ever touch an already-mapped row sharing the same
    // (HouseholdId, IntervalStart)?
    public static async Task Ac7NullPowerPointAsync(SpikeDbContext db, string providerName)
    {
        // Seed one already-mapped row deliberately colliding on (HouseholdId, IntervalStart) with
        // a row the null-PowerPoint batch below will also use — the isolation probe.
        var collisionTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        var seedPowerPointId = Guid.NewGuid();
        var seedImportId = await InsertParentRowAsync(db);
        var seedRow = new SpikeSmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = HouseholdId,
            PowerPointId = seedPowerPointId,
            IntervalStart = collisionTimestamp,
            IntervalEnd = collisionTimestamp.AddMinutes(10),
            KwhValue = 0.001234m,
            RoomName = "Kitchen",
            PowerPointName = "Kitchen - Fridge Circuit",
            DeviceName = "Eve Energy (Kitchen)",
            SmartPlugImportId = seedImportId,
        };
        db.SpikeSmartPlugReadings.Add(seedRow);
        await db.SaveChangesAsync();

        var importId = await InsertParentRowAsync(db);
        var batch = DataGenerator.GenerateNullPowerPointBatch(
            HouseholdId, importId, AnchorEnd, rowCount: 5_000).ToList();

        // Force exactly one row in the batch to collide with the seed row's (HouseholdId,
        // IntervalStart) — this is the case that must NOT touch the seed row.
        batch[0] = new SpikeSmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = HouseholdId,
            PowerPointId = null,
            IntervalStart = collisionTimestamp,
            IntervalEnd = collisionTimestamp.AddMinutes(10),
            KwhValue = 0.009999m,
            RoomName = batch[0].RoomName,
            PowerPointName = batch[0].PowerPointName,
            DeviceName = batch[0].DeviceName,
            SmartPlugImportId = importId,
        };

        var insertConfig = BaseConfig();
        var sw = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(batch, insertConfig);
        sw.Stop();
        ResultsLog.Record(providerName, "AC7-insert-5000-null-powerpoint", batch.Count, sw.Elapsed.TotalMilliseconds);

        var resubmission = batch.Select(r => new SpikeSmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = r.HouseholdId,
            PowerPointId = r.PowerPointId,
            IntervalStart = r.IntervalStart,
            IntervalEnd = r.IntervalEnd,
            KwhValue = r.KwhValue + 0.000001m,
            RoomName = r.RoomName,
            PowerPointName = r.PowerPointName,
            DeviceName = r.DeviceName,
            SmartPlugImportId = r.SmartPlugImportId,
        }).ToList();

        var updateConfig = BaseConfig();
        updateConfig.UpdateByProperties = [nameof(SpikeSmartPlugReading.HouseholdId), nameof(SpikeSmartPlugReading.IntervalStart)];

        var sw2 = Stopwatch.StartNew();
        await db.BulkInsertOrUpdateAsync(resubmission, updateConfig);
        sw2.Stop();
        ResultsLog.Record(providerName, "AC7-resubmit-5000-null-powerpoint", resubmission.Count, sw2.Elapsed.TotalMilliseconds);

        // The empirical check (AC #7's second half): the seeded already-mapped row must survive
        // untouched — same PowerPointId, same KwhValue as originally seeded.
        var survivor = await db.SpikeSmartPlugReadings
            .AsNoTracking()
            .Where(r => r.HouseholdId == HouseholdId && r.IntervalStart == collisionTimestamp && r.PowerPointId == seedPowerPointId)
            .SingleOrDefaultAsync();

        var isolationHolds = survivor is not null && survivor.KwhValue == seedRow.KwhValue;
        Console.WriteLine(isolationHolds
            ? "AC #7 ISOLATION CHECK: PASS — the [HouseholdId, IntervalStart] match key did not touch the already-mapped row sharing that timestamp."
            : "AC #7 ISOLATION CHECK: FAIL — the already-mapped row was overwritten or is missing. This is a genuine, reportable finding — do not paper over it.");
    }

    // AC #8: explicit-transaction parent-row + ~120k bulk insert, cancelled partway through.
    // Verifies both the "no partial row survives cancellation" invariant and the parent-row
    // atomicity claim.
    public static async Task Ac8CancellationRollbackAsync(SpikeDbContext db, string providerName, int cancelAfterMs)
    {
        var parentId = Guid.NewGuid();
        var batch = DataGenerator.GenerateSingleDeviceBatch(
            HouseholdId, Guid.NewGuid(), parentId, AnchorEnd).ToList();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(cancelAfterMs);

        bool observedCancellation;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.SpikeSmartPlugImports.Add(new SpikeSmartPlugImport
            {
                Id = parentId,
                HouseholdId = HouseholdId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(cts.Token);

            var config = BaseConfig();
            // Standard EFCore.BulkExtensions idiom for ambient-transaction participation —
            // BulkInsertOrUpdateAsync does not join SaveChangesAsync's pipeline on its own
            // (AD-23's own text); this is what makes the parent-row + bulk-write atomic.
            config.UnderlyingConnection = _ => db.Database.GetDbConnection();
            config.UnderlyingTransaction = _ => db.Database.CurrentTransaction!.GetDbTransaction();

            try
            {
                await db.BulkInsertOrUpdateAsync(batch, config, cancellationToken: cts.Token);
                observedCancellation = false;
                Console.WriteLine("WARNING: bulk insert completed before cancellation fired — increase --cancel-after-ms.");
                await tx.CommitAsync();
            }
            catch (OperationCanceledException)
            {
                observedCancellation = true;
                Console.WriteLine("Cancellation observed (OperationCanceledException) — rolling back, not committing.");
                await tx.RollbackAsync(CancellationToken.None);
            }
        }

        // Verify via a fresh, non-transactional connection — a separate DbContext instance.
        await using var verifyDb = new SpikeDbContext(db.Provider, db.ConnectionString);
        var readingCount = await verifyDb.SpikeSmartPlugReadings.CountAsync(r => r.SmartPlugImportId == parentId);
        var parentExists = await verifyDb.SpikeSmartPlugImports.AnyAsync(i => i.Id == parentId);

        var pass = observedCancellation && readingCount == 0 && !parentExists;
        Console.WriteLine(pass
            ? "AC #8 PASS: zero reading rows and zero parent row survived the cancelled, rolled-back transaction."
            : $"AC #8 FAIL/FINDING: observedCancellation={observedCancellation}, survivingReadingRows={readingCount}, parentRowExists={parentExists}. Record this as a genuine finding, do not paper over it.");

        ResultsLog.Record(providerName, "AC8-cancellation-rollback",
            batch.Count, cancelAfterMs, $"pass={pass}, observedCancellation={observedCancellation}, survivingRows={readingCount}, parentExists={parentExists}");
    }
}
