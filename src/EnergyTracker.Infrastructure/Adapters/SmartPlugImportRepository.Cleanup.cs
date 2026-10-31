using EnergyTracker.Application;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public partial class SmartPlugImportRepository
{
    public async Task SweepExpiredAsync(Guid householdId, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        // Eligible: a terminal job (Failed, or Completed with an import Completed/FlaggedForReview) before the cutoff.
        // Needs Mapping is excluded. LEFT JOIN, since a Failed job can have no import; the cutoff uses the import's own
        // CompletedAtUtc. Oldest first with a job.Id tiebreaker, bounded by Take in the query itself (CAP-6, ADR 0007).
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

        // Only this bounded take is deleted per call; the next poll's fresh query finds the rest, so no cursor is needed.
        await DeleteBoundedChunkAsync(householdId, boundedEligible, cancellationToken);
    }

    private readonly record struct EligibleRow(Guid BackgroundJobId, Guid? SmartPlugImportId);

    // Bounded counterpart of DeleteEligibleAsync for the sweep. The per-Household lock comes first so a losing poll does no
    // reading-count work; if it is not acquired, another poll is mid-chunk and this call does nothing. ADR 0007.
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

        // The first chunk of the greedy packer depends only on the prefix, so packing this bounded prefix gives the same first chunk.
        var importIdChunk = ChunkImportIdsByReadingVolume(boundedImportIds, readingCounts, DeleteReadingVolumeThreshold, DeleteBatchSize)
            .FirstOrDefault() ?? [];
        var importIdChunkSet = importIdChunk.ToHashSet();

        // Import-less Failed jobs cost nothing to delete and clear with this call; every row still clears within a bounded number of calls.
        var jobIdChunk = boundedEligible
            .Where(x => x.SmartPlugImportId is null || importIdChunkSet.Contains(x.SmartPlugImportId!.Value))
            .Select(x => x.BackgroundJobId)
            .ToList();

        await DeleteImportChunkAsync(importIdChunk, cancellationToken);

        // Jobs last: SmartPlugImport.BackgroundJobId is Restrict. The caller already bounded this to DeleteBatchSize rows.
        await dbContext.BackgroundJobs
            .Where(j => jobIdChunk.Contains(j.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Non-blocking advisory lock keyed by Household, scoped to the current transaction on both providers so commit or
    // rollback releases it. Provider-specific because neither primitive has a portable EF Core equivalent. ADR 0007.
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
        // hashtextextended() gives the 64-bit key; a collision only makes one poll skip a chunk. The interpolated value is a real
        // parameter (AD-2). ADR 0007.
        return await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({householdId.ToString()}, 0)) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private async Task<bool> TryAcquireHouseholdSweepLockSqlServerAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // sp_getapplock with @LockTimeout = 0 and @LockOwner = 'Transaction'. Return code 0 or 1 means acquired.
        // The batch is non-composable, so run it with ToListAsync() and take the single row client-side. ADR 0007.
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
        // Manual counterpart of the sweep: any state is eligible and cutoffUtc is optional. Its own query, so the
        // sweep's rules cannot regress. Age: import.CompletedAtUtc, else job.CompletedAtUtc, else job.CreatedAtUtc.
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

    // Bounds the typical per-command row and log volume of a chunked delete. 200 is a starting value from the incident
    // evidence, not a benchmark. ADR 0007.
    internal const int DeleteBatchSize = 200;

    // Also bounds the chunk by cumulative reading count, because the SetNull cascade cost scales with readings. Starting value
    // from the incident, not a benchmark. A single larger import forms its own chunk. ADR 0007.
    internal const int DeleteReadingVolumeThreshold = 20_000;

    // Detach the readings in DeleteBatchSize batches before deleting the import, so the DELETE has no cascade to do.
    // Rows inserted by a concurrent import after the last batch still fall back to the FK cascade (deferred-work.md). ADR 0007.
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

    // Greedy packing bounded by cumulative reading count and import count. Pure, so it is unit tested without rows.
    // An id without a count is zero; a single oversized id forms its own chunk; chunks are never empty.
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

    // Set based, in FK order, one transaction around all chunks (a failure rolls every chunk back). Chunking bounds each
    // command; CommandTimeout does not bound the transaction. Per-chunk transactions were rejected. ADR 0007.
    private async Task DeleteEligibleAsync(IReadOnlyList<Guid> jobIds, IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        // Reading counts are measured once, up front, in DeleteBatchSize chunks so the parameter count stays safe. Missing means zero.
        // DeleteBatchSize is reused deliberately: a second constant has no evidence it needs to differ.
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

        // Jobs last (Restrict FK). Chunked on their own: jobIds includes bare Failed jobs that no import chunk contains.
        foreach (var jobIdBatch in jobIds.Chunk(DeleteBatchSize))
        {
            await dbContext.BackgroundJobs
                .Where(j => jobIdBatch.Contains(j.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Shared by DeleteEligibleAsync and the bounded sweep so both keep the same cascade-bounding order (gaps, detach,
    // imports). The jobs delete stays with each caller.
    private async Task DeleteImportChunkAsync(IReadOnlyList<Guid> importIds, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps
            .Where(g => importIds.Contains(g.SmartPlugImportId))
            .ExecuteDeleteAsync(cancellationToken);

        // Detach first (see DetachReadingsForImportAsync) so the FK's SetNull has nothing left to do. A row inserted concurrently
        // after the last batch still falls back to the cascade (deferred-work.md). ADR 0007.
        foreach (var importId in importIds)
        {
            await DetachReadingsForImportAsync(importId, cancellationToken);
        }

        await dbContext.SmartPlugImports
            .Where(i => importIds.Contains(i.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
