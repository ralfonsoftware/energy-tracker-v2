using EnergyTracker.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-2/AD-6: plain portable EF Core LINQ only (ExecuteDeleteAsync/AddRangeAsync/SaveChangesAsync)
// — no EFCore.BulkExtensions/AD-23 mechanism (that AD's carve-out is scoped narrowly to
// SmartPlugImportRepository.AddAsync; reusing it here would need its own AD amendment, out of this
// story's scope). ChunkSize/CommandTimeout/one-outer-transaction all reuse
// SmartPlugImportRepository's own Story 3.8-3.10 incident-derived numbers/patterns (Dev Notes
// "Bulk-write lessons from Stories 3.8-3.10") rather than re-deriving new ones.
public class HouseholdRestoreWriter(EnergyTrackerDbContext dbContext) : IHouseholdRestoreWriter
{
    internal const int ChunkSize = 200;

    public async Task RestoreAsync(HouseholdRestoreData data, CancellationToken cancellationToken)
    {
        // Same per-call bump SmartPlugImportRepository.AddAsyncCore uses before its own bulk
        // operation — the app-wide default is tuned for point queries, not this shape.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120));

        // One outer transaction spans every chunk below (delete AND insert) — never per-chunk
        // transactions. This is what makes "never partially applied" true for the destructive
        // phase (Dev Notes).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        await DeleteExistingDataAsync(data.HouseholdId, cancellationToken);
        await InsertImportedDataAsync(data, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Reverse of InsertImportedDataAsync's insert order (Dev Notes' FK-dependency ordering,
    // confirmed against this codebase's real *Configuration.cs Fluent API files — every FK
    // between these entity types is DeleteBehavior.Restrict, never Cascade, so nothing here can
    // rely on an implicit cascade). SmartPlugReading is deleted before Device/PowerPoint
    // deliberately — Story 3.10's 4th incident was exactly this shape (a large child-row cascade
    // must never ride along as one unbounded operation on its parent's delete).
    private async Task DeleteExistingDataAsync(Guid householdId, CancellationToken cancellationToken)
    {
        await ChunkedDeleteAsync(
            ct => dbContext.AuditCorrections.AsNoTracking().Where(a => a.HouseholdId == householdId).Select(a => a.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.AuditCorrections.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.StatusSnapshots.AsNoTracking().Where(s => s.HouseholdId == householdId).Select(s => s.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.StatusSnapshots.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.SmartPlugReadings.AsNoTracking().Where(r => r.HouseholdId == householdId).Select(r => r.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.SmartPlugReadings.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.Events.AsNoTracking().Where(e => e.HouseholdId == householdId).Select(e => e.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.Events.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.Tariffs.AsNoTracking().Where(t => t.HouseholdId == householdId).Select(t => t.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.Tariffs.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.HouseholdMembers.AsNoTracking().Where(m => m.HouseholdId == householdId).Select(m => m.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.HouseholdMembers.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.MeterRegressionPrompts.AsNoTracking().Where(p => p.HouseholdId == householdId).Select(p => p.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.MeterRegressionPrompts.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.MeterReadings.AsNoTracking().Where(r => r.HouseholdId == householdId).Select(r => r.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.MeterReadings.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.Devices.AsNoTracking().Where(d => d.HouseholdId == householdId).Select(d => d.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.Devices.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.PowerPoints.AsNoTracking().Where(p => p.HouseholdId == householdId).Select(p => p.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.PowerPoints.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.Rooms.AsNoTracking().Where(r => r.HouseholdId == householdId).Select(r => r.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.Rooms.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);

        await ChunkedDeleteAsync(
            ct => dbContext.MainMeters.AsNoTracking().Where(m => m.HouseholdId == householdId).Select(m => m.Id).Take(ChunkSize).ToListAsync(ct),
            (ids, ct) => dbContext.MainMeters.Where(m => ids.Contains(m.Id)).ExecuteDeleteAsync(ct),
            cancellationToken);
    }

    // Query the ids first (bounded by ChunkSize-sized pages), then delete by id in the same sized
    // chunks — ExecuteDeleteAsync itself has no portable "skip N rows already deleted" shape across
    // both providers, so the chunk boundary is drawn over ids instead: every call re-queries "the
    // next ChunkSize ids still present", which naturally shrinks to zero as rows are deleted.
    private static async Task ChunkedDeleteAsync(
        Func<CancellationToken, Task<List<Guid>>> fetchNextChunkIds,
        Func<List<Guid>, CancellationToken, Task<int>> deleteByIds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var ids = await fetchNextChunkIds(cancellationToken);
            if (ids.Count == 0)
            {
                return;
            }

            await deleteByIds(ids, cancellationToken);
        }
    }

    // Household(update) → MainMeter → Room → PowerPoint → Device → MeterReading →
    // MeterRegressionPrompt → HouseholdMember → Tariff → Event → SmartPlugReading →
    // StatusSnapshot → AuditCorrection (Dev Notes, confirmed against the real *Configuration.cs
    // Fluent API files) — every dependency (MainMeter before MeterReading/MeterRegressionPrompt,
    // Room before PowerPoint, PowerPoint before Device/SmartPlugReading, MeterReading before
    // MeterRegressionPrompt) is satisfied by this order.
    private async Task InsertImportedDataAsync(HouseholdRestoreData data, CancellationToken cancellationToken)
    {
        await UpdateHouseholdSettingsAsync(data.HouseholdId, data.HouseholdSettings, cancellationToken);

        if (data.MainMeter is { } mainMeter)
        {
            await ChunkedInsertAsync(dbContext.MainMeters, [mainMeter], cancellationToken);
        }

        await ChunkedInsertAsync(dbContext.Rooms, data.Rooms, cancellationToken);
        await ChunkedInsertAsync(dbContext.PowerPoints, data.PowerPoints, cancellationToken);
        await ChunkedInsertAsync(dbContext.Devices, data.Devices, cancellationToken);
        await ChunkedInsertAsync(dbContext.MeterReadings, data.MeterReadings, cancellationToken);
        await ChunkedInsertAsync(dbContext.MeterRegressionPrompts, data.MeterRegressionPrompts, cancellationToken);
        await ChunkedInsertAsync(dbContext.HouseholdMembers, data.HouseholdMembers, cancellationToken);
        await ChunkedInsertAsync(dbContext.Tariffs, data.Tariffs, cancellationToken);
        await ChunkedInsertAsync(dbContext.Events, data.Events, cancellationToken);
        await ChunkedInsertAsync(dbContext.SmartPlugReadings, data.SmartPlugReadings, cancellationToken);
        await ChunkedInsertAsync(dbContext.StatusSnapshots, data.StatusSnapshots, cancellationToken);
        await ChunkedInsertAsync(dbContext.AuditCorrections, data.AuditCorrections, cancellationToken);
    }

    // Household's own row is the one exception to "delete then insert" (Task 3) — updated in
    // place, Id/CreatedAtUtc never touched. AiPlausibilityBackendConfigured/Label are deliberately
    // NOT read from the patch at all (AD-19: deployment-level, this deployment's own
    // AiPlausibilityBackendOptions config is authoritative). Version++ mirrors
    // HouseholdRepository.UpdateYearlyBaselineAsync's own convention — no expectedVersion check
    // here since this is an internal system operation, not a client-driven edit with a known prior
    // version.
    private async Task UpdateHouseholdSettingsAsync(Guid householdId, HouseholdSettingsPatch patch, CancellationToken cancellationToken)
    {
        var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
        household.Locale = patch.Locale;
        household.Currency = patch.Currency;
        household.YearlyBaselineKwh = patch.YearlyBaselineKwh;
        household.TrendingThresholdKwh = patch.TrendingThresholdKwh;
        household.LowConfidenceGapDays = patch.LowConfidenceGapDays;
        household.TariffCheckCadenceMonths = patch.TariffCheckCadenceMonths;
        household.AiPlausibilityEnabled = patch.AiPlausibilityEnabled;
        household.Version++;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Chunked AddRangeAsync+SaveChangesAsync, never a single unbounded call — same chunking
    // discipline as the deletes above. Detaches just this chunk's own newly-tracked entities
    // afterward, never a blanket ChangeTracker.Clear() — SmartPlugImportRepository.AddAsync's own
    // incident-fix comment documents exactly why a blanket clear is unsafe on this shared scoped
    // DbContext: BackgroundJobProcessor tracks its own BackgroundJob row across this entire call,
    // and a blanket clear would silently detach it too.
    private async Task ChunkedInsertAsync<TEntity>(DbSet<TEntity> set, IReadOnlyList<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class
    {
        for (var offset = 0; offset < entities.Count; offset += ChunkSize)
        {
            var chunk = entities.Skip(offset).Take(ChunkSize).ToList();
            await set.AddRangeAsync(chunk, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var entity in chunk)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
            }
        }
    }
}
