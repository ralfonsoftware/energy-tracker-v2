using System.Data.Common;
using EFCore.BulkExtensions;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public partial class SmartPlugImportRepository(
    EnergyTrackerDbContext dbContext, IAuditCorrectionRecorder auditCorrectionRecorder,
    ILogger<SmartPlugImportRepository> logger) : ISmartPlugImportRepository
{
    // Ties UpsertAwaitingMappingReadingsAsync's chunk size to BuildAwaitingMappingValuesClause's
    // own column count and each provider's per-statement parameter ceiling (Story 3.9 review fix)
    // — previously two independent magic numbers (9 columns, 5000/200-row chunk sizes) with
    // nothing tying them together, so a future column added to BuildAwaitingMappingValuesClause
    // could silently reintroduce the exact "blew past the provider's parameter limit" bug this
    // chunking exists to fix. Postgres's hard per-statement limit; SQL Server's practical ceiling.
    private const int PostgresMaxStatementParameters = 65_535;
    private const int SqlServerPracticalMaxStatementParameters = 2_100;
    private const int AwaitingMappingColumnsPerRow = 9;


    public Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.BackgroundJobId == backgroundJobId, cancellationToken);

    public Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == smartPlugImportId, cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    public async Task<SmartPlugReadingWatermark?> FindLatestReadingWatermarkByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderByDescending(r => r.IntervalStart)
            .Select(r => new SmartPlugReadingWatermark(r.Id, r.IntervalStart, r.KwhValue))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, DateOnly sinceDate, CancellationToken cancellationToken)
    {
        // AD-9: SmartPlugReading.IntervalStart is a local-time date encoded with a zero UTC offset
        // — match that encoding here rather than comparing against a real-offset instant.
        var sinceInstant = new DateTimeOffset(sinceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != excludeSmartPlugImportId
                && r.IntervalStart >= sinceInstant)
            .OrderBy(r => r.IntervalStart)
            .ToListAsync(cancellationToken);
    }

    public async Task<DateOnly?> FindFirstReadingDateByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken)
    {
        var first = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderBy(r => r.IntervalStart)
            .Select(r => (DateTimeOffset?)r.IntervalStart)
            .FirstOrDefaultAsync(cancellationToken);
        return first is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    }

    public async Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps.AddRangeAsync(gaps, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .Where(g => g.SmartPlugImportId == smartPlugImportId)
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdsAsync(
        IReadOnlyList<Guid> smartPlugImportIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .AsNoTracking()
            .Where(g => smartPlugImportIds.Contains(g.SmartPlugImportId))
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SmartPlugImportGaps.AddAsync(gap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImport>> FindAllByBackgroundJobIdsAsync(
        IReadOnlyList<Guid> backgroundJobIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImports
            .AsNoTracking()
            .Where(i => backgroundJobIds.Contains(i.BackgroundJobId))
            .ToListAsync(cancellationToken);

    public async Task SweepExpiredAsync(Guid householdId, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        // Eligible-for-deletion rule (Story 3.6/AD-6 extension): the job reached a terminal,
        // resolved state — Error (BackgroundJobStatus.Failed) or Success/Flagged for Review
        // (BackgroundJobStatus.Completed with the joined SmartPlugImport.Status Completed or
        // FlaggedForReview) — before the cutoff. Needs Mapping (AwaitingPowerPointMapping) is
        // deliberately excluded here even though the background job itself is Completed — the
        // import is still unresolved (AC #7).
        //
        // LEFT JOIN (not inner) — review-round-2 patch: a Failed job can have no paired
        // SmartPlugImport row at all (e.g. an unknown JobType, or a JSON-deserialize failure
        // inside BackgroundJobProcessor before ProcessSmartPlugImport.ExecuteAsync's own
        // paired-row-on-failure logic ever runs). An inner join silently excluded that class of
        // job from the sweep forever.
        //
        // The cutoff compares against the import row's own CompletedAtUtc when one exists, not
        // the BackgroundJob row's — review-round-2 patch: MapSmartPlugImportToPowerPoint updates
        // only the import's CompletedAtUtc when a Needs Mapping job is later resolved, so
        // comparing against the job's original (parse-time) CompletedAtUtc would sweep a
        // just-resolved import on the very next list read whenever the original parse happened
        // more than 30 days ago.
        //
        // CAP-6 (2026-09-17): ordered oldest-eligible-first, with a `job.Id` tiebreaker for
        // determinism when two rows share the exact same completedAtUtc (plausible for imports
        // parsed in the same batch) — and bounded to DeleteBatchSize rows at the QUERY level via
        // `Take` before `ToListAsync`, not a client-side `.Take()` on an already-materialized list.
        // This sweep runs inline on every GET poll, so even the SELECT that decides what's eligible
        // must stay bounded regardless of total backlog size, not just the delete work that follows.
        var boundedEligible = await (
            from job in dbContext.BackgroundJobs
            where job.HouseholdId == householdId && job.JobType == JobTypes.ProcessSmartPlugImport
            join import in dbContext.SmartPlugImports on job.Id equals import.BackgroundJobId into importGroup
            from import in importGroup.DefaultIfEmpty()
            let completedAtUtc = import != null ? import.CompletedAtUtc : job.CompletedAtUtc
            where completedAtUtc != null && completedAtUtc < cutoffUtc
                && (job.Status == BackgroundJobStatus.Failed
                    || (job.Status == BackgroundJobStatus.Completed && import != null
                        && (import.Status == SmartPlugImportStatus.Completed || import.Status == SmartPlugImportStatus.FlaggedForReview)))
            orderby completedAtUtc, job.Id
            select new EligibleRow(job.Id, import == null ? null : import.Id)
        ).Take(DeleteBatchSize).ToListAsync(cancellationToken);

        if (boundedEligible.Count == 0)
        {
            return;
        }

        // Unlike DeleteJobsAsync/DeleteEligibleAsync below (which process every eligible row in one
        // call), only this already-query-bounded take is even considered for deletion this call —
        // any remainder is left for the next poll's fresh eligibility query. No persisted cursor is
        // needed for that resume: a row this call deletes simply stops matching the query next time.
        await DeleteBoundedChunkAsync(householdId, boundedEligible, cancellationToken);
    }

    private readonly record struct EligibleRow(Guid BackgroundJobId, Guid? SmartPlugImportId);

    // CAP-6: bounded counterpart to DeleteEligibleAsync, used only by SweepExpiredAsync's per-call-
    // chunked path above. Guarded by a non-blocking, transaction-scoped per-household advisory lock:
    // without it, two concurrent GET polls for the same household (e.g. two open tabs) could both
    // select the same oldest-eligible chunk and race their deletes, with the loser blocking on DB row
    // locks for the duration of the winner's still-in-flight DetachReadingsForImportAsync loop —
    // bounded by one chunk's own duration, but still a real, avoidable latency spike for exactly the
    // population this spec protects. If the lock isn't acquired, another poll for this household is
    // already mid-chunk; this call no-ops and the next poll (≤8s later) retries against then-current
    // state.
    //
    // Round-2 review finding (Blind Hunter): the lock is acquired FIRST, before measuring reading
    // counts or packing a chunk — a losing poller must short-circuit as cheaply as possible. The
    // reading-count GROUP BY below is bounded by import count (≤DeleteBatchSize, inherited from the
    // caller's query-level Take) but not by reading volume, so running it unconditionally — even for
    // a call about to lose the lock race — could scan a large volume inline on every contended poll.
    // Acquiring the lock up front means only the winner ever pays that cost.
    private async Task DeleteBoundedChunkAsync(Guid householdId, IReadOnlyList<EligibleRow> boundedEligible, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryAcquireHouseholdSweepLockAsync(householdId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var boundedImportIds = boundedEligible.Where(x => x.SmartPlugImportId is not null).Select(x => x.SmartPlugImportId!.Value).ToList();

        var readingCounts = boundedImportIds.Count > 0
            ? await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId != null && boundedImportIds.Contains(r.SmartPlugImportId!.Value))
                .GroupBy(r => r.SmartPlugImportId!.Value)
                .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken)
            : new Dictionary<Guid, int>();

        // Reproduces exactly what DeleteEligibleAsync's own full-sweep packer would yield as its
        // first chunk: the greedy packer's first boundary depends only on the prefix of importIds up
        // to whichever cap trips first, never on anything after it — so feeding it just this
        // already-bounded prefix and taking its first chunk is equivalent, without measuring or
        // packing a potentially large remaining backlog.
        var importIdChunk = ChunkImportIdsByReadingVolume(boundedImportIds, readingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize)
            .FirstOrDefault() ?? [];
        var importIdChunkSet = importIdChunk.ToHashSet();

        // Import-less Failed jobs (no paired SmartPlugImport row) carry zero reading cost, so every
        // one in the bounded take clears this call regardless of the import chunk boundary above —
        // this can, in principle, clear a newer bare-Failed-job row while an older, volume-deferred
        // import waits for a later call. Accepted: every row still clears within a bounded number of
        // calls (no permanent starvation), and forcing bare jobs to wait behind volume-bound imports
        // would only delay free work for no correctness benefit.
        var jobIdChunk = boundedEligible
            .Where(x => x.SmartPlugImportId is null || importIdChunkSet.Contains(x.SmartPlugImportId!.Value))
            .Select(x => x.BackgroundJobId)
            .ToList();

        await DeleteImportChunkAsync(importIdChunk, cancellationToken);

        // BackgroundJobs last — SmartPlugImport.BackgroundJobId's FK is Restrict, so any paired
        // import row must already be gone before this delete can succeed. Already bounded to at most
        // DeleteBatchSize rows by the caller, so unlike DeleteEligibleAsync's own jobIds loop below,
        // no further internal chunking is needed here.
        await dbContext.BackgroundJobs
            .Where(j => jobIdChunk.Contains(j.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Non-blocking, transaction-scoped advisory lock keyed by household, used only by
    // DeleteBoundedChunkAsync above. Scoped to the CURRENT transaction on both providers so it
    // releases automatically on commit or rollback — no separate unlock call to forget, no risk from
    // connection pooling returning a connection to the pool while a session-scoped lock is still
    // held. Provider-specific because neither primitive has a portable EF Core equivalent — same
    // reason UpsertAwaitingMappingReadingsAsync above branches by provider.
    private Task<bool> TryAcquireHouseholdSweepLockAsync(Guid householdId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return TryAcquireHouseholdSweepLockPostgresAsync(householdId, cancellationToken);
        }

        if (dbContext.Database.IsSqlServer())
        {
            return TryAcquireHouseholdSweepLockSqlServerAsync(householdId, cancellationToken);
        }

        throw new InvalidOperationException(
            $"No advisory-lock implementation is defined for database provider '{dbContext.Database.ProviderName}'.");
    }

    private async Task<bool> TryAcquireHouseholdSweepLockPostgresAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // pg_try_advisory_xact_lock takes a bigint key; hashtextextended() derives one from the
        // household Guid's string form using the full 64-bit output (there's no direct Guid-keyed
        // advisory lock function) — chosen over the plain hashtext()'s 32-bit output for the same
        // cost, to keep collision probability as low as the primitive allows. A hash collision
        // between two different households' lock keys would only cause one to transiently no-op this
        // call's chunk (it self-heals on the next poll), never incorrect data — an acceptable,
        // extremely low-probability residual, the same class of accepted risk as this file's other
        // starting-value/evidence-based tuning choices. Parameterized via string interpolation (EF
        // Core rewrites the hole to a real parameter, never concatenates it — AD-2).
        return await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({householdId.ToString()}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private async Task<bool> TryAcquireHouseholdSweepLockSqlServerAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // sp_getapplock is SQL Server's closest non-blocking, transaction-scoped equivalent to
        // Postgres's pg_try_advisory_xact_lock: @LockTimeout = 0 makes acquisition non-blocking,
        // @LockOwner = 'Transaction' releases it automatically on commit/rollback. Return code 0 or 1
        // means acquired; any negative code (timeout, cancel, deadlock, parameter error) means not
        // acquired — treated the same as "another poll is already mid-chunk" either way.
        //
        // Round-2 review finding: this batch (DECLARE/EXEC before the final SELECT) is non-composable
        // SQL, and SqlQuery<T>().SingleAsync() tries to compose (wrap it as a subquery) to apply its
        // own TOP/LIMIT — throwing InvalidOperationException on every call. ToListAsync() executes the
        // raw batch as-is with no composition; taking Single() over the materialized (always
        // one-row) result client-side avoids it. Caught only once this file gained its first-ever
        // SqlServer test exercising this method — this exact call would otherwise have thrown on
        // every SweepExpiredAsync invocation against the real production (SqlServer) database.
        var lockResults = await dbContext.Database
            .SqlQuery<int>($"""
                DECLARE @LockResult int;
                EXEC @LockResult = sp_getapplock
                    @Resource = {householdId.ToString()},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 0;
                SELECT @LockResult AS "Value";
                """)
            .ToListAsync(cancellationToken);
        return lockResults.Single() >= 0;
    }

    public async Task<int> DeleteJobsAsync(Guid householdId, DateTimeOffset? cutoffUtc, CancellationToken cancellationToken)
    {
        // Story 3.10: a manual, user-triggered counterpart to SweepExpiredAsync above — deliberately
        // NOT restricted to terminal states (Waiting/Processing/Needs Mapping are eligible here,
        // unlike the automatic sweep) and cutoffUtc is optional ("clean up everything" = null).
        // Kept as its own eligibility query rather than folding an "include active states" flag
        // into SweepExpiredAsync itself, so the automatic sweep's Story 3.6 behavior can never
        // regress via this method's changes.
        //
        // effectiveAgeUtc fallback chain: import.CompletedAtUtc (set at initial parse time for
        // BOTH Completed and AwaitingPowerPointMapping — see ProcessSmartPlugImport.cs:113-124, so
        // Needs Mapping rows already have a usable value here) -> job.CompletedAtUtc (Failed jobs
        // with no paired import row) -> job.CreatedAtUtc (the only timestamp a Queued/Processing
        // job has — both of the prior two are null for those).
        var eligible = await (
            from job in dbContext.BackgroundJobs
            where job.HouseholdId == householdId && job.JobType == JobTypes.ProcessSmartPlugImport
            join import in dbContext.SmartPlugImports on job.Id equals import.BackgroundJobId into importGroup
            from import in importGroup.DefaultIfEmpty()
            let effectiveAgeUtc = (import != null ? import.CompletedAtUtc : null) ?? job.CompletedAtUtc ?? job.CreatedAtUtc
            where cutoffUtc == null || effectiveAgeUtc < cutoffUtc
            select new { BackgroundJobId = job.Id, SmartPlugImportId = (Guid?)(import == null ? null : import.Id) }
        ).ToListAsync(cancellationToken);

        if (eligible.Count == 0)
        {
            return 0;
        }

        var importIds = eligible.Where(x => x.SmartPlugImportId is not null).Select(x => x.SmartPlugImportId!.Value).ToList();
        var jobIds = eligible.Select(x => x.BackgroundJobId).ToList();
        await DeleteEligibleAsync(jobIds, importIds, cancellationToken);

        return eligible.Count;
    }

    // Incident fix (2026-09-12 prod): bounds the *typical* per-command row/log volume of each
    // chunked delete below. This is NOT a hard bound on every possible case: a single import can
    // itself carry hundreds of thousands of SmartPlugReading rows (a full-history Eve Home export
    // — see AddAsyncCore's BulkCopyTimeout comment above), and chunking by import COUNT doesn't
    // bound one pathologically large import's own SetNull cascade — only the number of imports
    // touched per command. This fix reduces risk for the incident's actual shape (many accumulated
    // jobs/imports over time saturating Azure SQL Basic-tier's 5-DTU log-write throughput within
    // the 120s CommandTimeout on "clean up everything" / DeleteJobsAsync with cutoffUtc: null) —
    // it does not claim to solve the different edge case of one extreme-sized individual import
    // (accepted residual risk, tracked in deferred-work.md, same as this fix's other known limit:
    // no bound on total wall-clock time across every chunk). 200 is a starting value from the
    // incident's own DTU/Log-IO evidence, not a benchmarked optimum. It's also comfortably under
    // SQL Server's ~2100-parameter ceiling — confirmed to actually matter for this query shape, not
    // a theoretical concern: the 2026-09-12 incident's own captured DbCommand log shows this exact
    // `Contains(...)` predicate translating to one named parameter per id (`@importIds1` ...
    // `@importIds60` observed live), not a single array/JSON parameter — but that ceiling still
    // wasn't the actual constraint the incident hit (the cascade's row/log volume was).
    internal const int DeleteBatchSize = 200;

    // Incident fix round 2 (2026-09-12 prod, same day): DeleteBatchSize alone didn't fix the
    // incident it was built for — a household with only 51-60 eligible rows (well under 200)
    // never triggers count-based chunking at all, yet 5 of its 51 imports individually carried
    // 57,171/66,238/112,005/114,077/122,158 SmartPlugReading rows (487,380 total), and the single
    // resulting DELETE FROM SmartPlugImports command still saturated Azure SQL Basic-tier and hit
    // CommandTimeout. This bounds the importIds loop by cumulative READING count too, not just
    // import count — see ChunkImportIdsByReadingVolume below. 20,000 is roughly 24x smaller than
    // the volume that saturated Basic-tier for ~2 minutes in the confirmed incident; a starting
    // value from that evidence, not a benchmarked optimum. A single import whose own reading count
    // alone exceeds this still forms its own (unsplittable) chunk — accepted, not solved here; see
    // deferred-work.md.
    internal const int DeleteReadingVolumeThreshold = 20_000;

    // Incident fix round 4 (2026-09-12 20:40 prod, confirmed via Container App logs + Azure
    // Monitor DTU/Log-IO): CAP-4's own round-3 spec accepted "a single import whose own reading
    // count exceeds DeleteReadingVolumeThreshold still can't be split further" as low-stakes
    // residual risk, reasoning the household's largest single import (122,158 rows) was "well
    // within a reasonable chunk threshold on its own." That assumption was never actually load-
    // tested against a live SetNull cascade and was wrong: isolating that one 122,158-row import
    // alone in its own chunk (exactly what CAP-4 does) still blew Azure SQL Basic-tier's 120s
    // CommandTimeout — confirmed by the exact failing command in production logs
    // (`Failed executing DbCommand (120,205ms) [Parameters=[..., @importIdBatch1=...]`, a single-
    // import chunk) and matching DTU (94.5%)/Log IO (93%) saturation. Round 3's async move (CAP-5)
    // only removed the *HTTP*-ingress timeout ceiling; it did nothing about the *SQL* CommandTimeout
    // a single command's own cascade can still hit. DetachReadingsForImportAsync below closes this
    // for good: instead of relying on the FK's SetNull cascade to update however many rows a given
    // import has in one opaque, unboundable server-side operation, every import's own readings are
    // explicitly detached in DeleteBatchSize-sized batches *before* that import is deleted — so the
    // DELETE FROM SmartPlugImports that follows always matches zero children and costs nothing
    // regardless of how large the import was. This makes ChunkImportIdsByReadingVolume's own
    // reading-volume bound no longer necessary to prevent a timeout (every import's cascade cost is
    // now always ~0), but it's left in place unchanged — still harmless, and safer to leave working,
    // already-tested chunking logic alone than to remove it under incident pressure.
    //
    // Round-4 review findings (Blind Hunter): this trades one unboundable command for roughly
    // readingCount/DeleteBatchSize round trips (~1,200 SELECT+UPDATE pairs for a 122,158-row
    // import) — an unbenchmarked aggregate wall-clock cost, same "starting value from evidence, not
    // a benchmarked optimum" caveat DeleteBatchSize/DeleteReadingVolumeThreshold's own comments
    // already carry, not a new gap. Two residual risks this doesn't close, tracked in
    // deferred-work.md: (1) an import still Processing/AwaitingPowerPointMapping — both eligible for
    // DeleteJobsAsync by design — can keep receiving new SmartPlugReading rows via AddAsyncCore
    // concurrently with this loop; a row inserted after this loop already passed its offset is
    // missed by this detach pass and falls back to the FK's own SetNull cascade at delete time (this
    // narrows that pre-existing TOCTOU's blast radius to just the race-window rows, it does not
    // close it); (2) the automatic SweepExpiredAsync path (unlike the manual "clean up everything"
    // endpoint) was never moved off the synchronous HTTP request by CAP-5, so this loop's aggregate
    // duration is now also paid synchronously, inline, by whichever GET /api/smart-plug-import-jobs
    // request happens to trigger the sweep for a large aging import.
    private async Task DetachReadingsForImportAsync(Guid importId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var readingIds = await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId == importId)
                .Select(r => r.Id)
                .Take(DeleteBatchSize)
                .ToListAsync(cancellationToken);
            if (readingIds.Count == 0)
            {
                return;
            }

            await dbContext.SmartPlugReadings
                .Where(r => readingIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SmartPlugImportId, (Guid?)null), cancellationToken);
        }
    }

    // Greedily packs importIds into chunks bounded by BOTH cumulative reading count and import
    // count, whichever is hit first — pure and DB-free so the packing logic itself gets direct
    // unit coverage (SmartPlugImportRepositoryChunkingTests.cs) without seeding real rows. An id
    // missing from readingCountByImportId (no readings) is treated as zero. A single id whose own
    // count already exceeds maxReadingsPerChunk still lands alone in its own chunk — chunks are
    // never empty and every input id is assigned to exactly one chunk.
    internal static IEnumerable<Guid[]> ChunkImportIdsByReadingVolume(
        IReadOnlyList<Guid> importIds, IReadOnlyDictionary<Guid, int> readingCountByImportId,
        int maxReadingsPerChunk, int maxImportsPerChunk)
    {
        var currentChunk = new List<Guid>();
        var currentReadingCount = 0;
        foreach (var importId in importIds)
        {
            var readingCount = readingCountByImportId.GetValueOrDefault(importId);
            if (currentChunk.Count > 0 &&
                (currentChunk.Count >= maxImportsPerChunk || currentReadingCount + readingCount > maxReadingsPerChunk))
            {
                yield return currentChunk.ToArray();
                currentChunk = [];
                currentReadingCount = 0;
            }

            currentChunk.Add(importId);
            currentReadingCount += readingCount;
        }

        if (currentChunk.Count > 0)
        {
            yield return currentChunk.ToArray();
        }
    }

    // Shared by SweepExpiredAsync (automatic, terminal-states-only) and DeleteJobsAsync (manual,
    // all-states, Story 3.10) — set-based, in FK-dependency order (UpdateMappingAsync's own doc
    // comment establishes the same discipline for this table) — never load-then-remove, these
    // tables can hold hundreds of thousands of rows.
    //
    // Code-review fix (2026-09-11): wrapped in one explicit transaction — three independent
    // ExecuteDeleteAsync calls with no transaction risked a partial delete (gaps/imports gone,
    // BackgroundJob rows left behind) surviving a cancellation or transient failure between calls,
    // the same class of bug this codebase already fixed once for Power Point mapping (fa77aef).
    //
    // Incident fix (2026-09-12): each of the three deletes is now chunked into DeleteBatchSize-id
    // batches, each its own ExecuteDeleteAsync command, still inside this same outer transaction.
    // CommandTimeout (Program.cs) bounds a single command's execution, not the transaction's total
    // duration, so chunking removes the per-command timeout risk without weakening the all-or-
    // nothing guarantee above — a failure on any chunk still rolls back every chunk, including ones
    // that already ran, because the transaction is never committed. Per-chunk transactions were
    // considered and rejected: they would weaken that guarantee for no timeout benefit
    // CommandTimeout doesn't already give per-command. The two loops below chunk for two distinct
    // reasons sharing one constant: the importIds loop bounds a downstream SetNull cascade (see
    // DeleteBatchSize's own comment); the jobIds loop has no such cascade to bound at all
    // (SmartPlugImport.BackgroundJobId's FK is Restrict, and nothing else references
    // BackgroundJobs) — it's chunked purely to bound that delete statement's own direct row/log
    // volume. Unlike UpsertAwaitingMappingReadingsAsync's provider-specific chunk sizes (5000
    // Postgres / 200 SqlServer, driven by each provider's differing per-statement parameter
    // ceiling), the bottleneck here is cascade/log-volume, not parameter count — not meaningfully
    // asymmetric across providers, so one shared constant for both loops is deliberate.
    private async Task DeleteEligibleAsync(IReadOnlyList<Guid> jobIds, IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        // Incident fix round 2: measured once, up front — the cost driver is how many
        // SmartPlugReading rows each import's SetNull cascade will touch, not how many imports
        // there are. Missing from this dictionary (queried only for ids in importIds) means zero
        // readings; ChunkImportIdsByReadingVolume treats it that way.
        //
        // Round-2 review finding (Blind Hunter + Edge Case Hunter, independently): a single
        // GROUP BY against the full, unchunked importIds list reuses the exact Contains(...) shape
        // DeleteBatchSize's own comment confirmed translates to one SQL parameter per id — for a
        // household with enough eligible imports, that alone risks SQL Server's ~2100-parameter
        // ceiling before the (correctly chunked) delete loop even starts. Measuring in
        // DeleteBatchSize-sized chunks too reuses that already-proven-safe parameter count instead
        // of introducing a second, unbounded query shape.
        //
        // Round-3 review finding (Blind Hunter): DeleteBatchSize's own value was chosen for a
        // different reason (cascade/log-volume per delete command, see its own comment) than what
        // this measurement query actually needs (parameter-count safety per read command) — the
        // two constraints are independent and DeleteBatchSize's value happens to satisfy both, not
        // because it was derived for this purpose. A deliberate reuse, not an oversight: introducing
        // a second constant here would add a knob with no evidence it needs to differ in practice.
        var importReadingCounts = new Dictionary<Guid, int>();
        foreach (var measurementBatch in importIds.Chunk(DeleteBatchSize))
        {
            var batchCounts = await dbContext.SmartPlugReadings
                .Where(r => r.SmartPlugImportId != null && measurementBatch.Contains(r.SmartPlugImportId!.Value))
                .GroupBy(r => r.SmartPlugImportId!.Value)
                .Select(g => new { ImportId = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            foreach (var x in batchCounts)
            {
                importReadingCounts[x.ImportId] = x.Count;
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var importIdBatch in ChunkImportIdsByReadingVolume(importIds, importReadingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize))
        {
            await DeleteImportChunkAsync(importIdBatch, cancellationToken);
        }

        // BackgroundJobs last — SmartPlugImport.BackgroundJobId's FK is Restrict, so any paired
        // import row must already be gone before this delete can succeed. Chunked independently from
        // the importIds loop above (own DeleteBatchSize-sized batches over the full jobIds list, not
        // one batch per importIdBatch): jobIds also includes bare Failed jobs with no paired import
        // at all, which never appear in any importIdBatch (ChunkImportIdsByReadingVolume only ever
        // packs importIds), so this loop's chunk boundaries are structurally independent of the
        // import-volume-based ones above and can't be folded into the same per-chunk unit.
        foreach (var jobIdBatch in jobIds.Chunk(DeleteBatchSize))
        {
            await dbContext.BackgroundJobs
                .Where(j => jobIdBatch.Contains(j.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // CAP-6: extracted from DeleteEligibleAsync's own per-chunk loop body so SweepExpiredAsync's
    // bounded path (DeleteBoundedChunkAsync above) reuses the exact same cascade-bounding delete
    // order instead of drifting independently — this is the part worth sharing (gaps -> detach ->
    // imports, the reading-volume-bounded cascade this file's whole incident-fix history is about);
    // the jobs delete step deliberately stays out of this helper and lives separately in each caller
    // (see the comment on DeleteEligibleAsync's own jobIds loop above for why).
    private async Task DeleteImportChunkAsync(IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps
            .Where(g => importIds.Contains(g.SmartPlugImportId))
            .ExecuteDeleteAsync(cancellationToken);

        // Incident fix round 4: explicitly detach each import's own SmartPlugReading rows in bounded
        // batches (see DetachReadingsForImportAsync above) before deleting the import itself — for a
        // terminal-state import this leaves nothing for the FK's SetNull behavior (Task 3/Story 3.6,
        // AD-20) to do at delete time. It can still be the one doing real work for a still-
        // Processing/AwaitingPowerPointMapping import (both eligible for DeleteJobsAsync by design): a
        // new SmartPlugReading row inserted concurrently via AddAsyncCore, after this loop's last
        // batch already observed zero remaining, is missed by this pass and falls back to the FK
        // cascade — same pre-existing TOCTOU already tracked in deferred-work.md, narrowed to just
        // the race-window rows, not closed by this fix.
        foreach (var importId in importIds)
        {
            await DetachReadingsForImportAsync(importId, cancellationToken);
        }

        await dbContext.SmartPlugImports
            .Where(i => importIds.Contains(i.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
