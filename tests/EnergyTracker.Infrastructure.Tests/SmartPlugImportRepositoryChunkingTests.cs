using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Round-2 incident fix (2026-09-12 prod): ChunkImportIdsByReadingVolume is the packing algorithm
// bounding the SmartPlugImports -> SmartPlugReading SetNull cascade by cumulative reading count,
// not just import count (the round-1 fix's gap). Pure, DB-free unit coverage — no need to seed
// tens of thousands of real rows to prove this algorithm's correctness.
public class SmartPlugImportRepositoryChunkingTests
{
    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    [Fact]
    public void Many_tiny_imports_still_respect_the_import_count_cap()
    {
        var ids = Ids(5);
        var readingCounts = ids.ToDictionary(id => id, _ => 1);

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume(ids, readingCounts, maxReadingsPerChunk: 1_000_000, maxImportsPerChunk: 2)
            .ToList();

        chunks.Count.ShouldBe(3);
        chunks[0].Length.ShouldBe(2);
        chunks[1].Length.ShouldBe(2);
        chunks[2].Length.ShouldBe(1);
        chunks.SelectMany(c => c).ShouldBe(ids, ignoreOrder: false);
    }

    [Fact]
    public void Incident_shaped_large_imports_each_land_in_their_own_chunk()
    {
        // Mirrors the confirmed 2026-09-12 incident's actual numbers: 5 imports each individually
        // over the volume threshold, packed among many small ones.
        var largeIds = Ids(5);
        var smallIds = Ids(46);
        var readingCounts = new Dictionary<Guid, int>();
        int[] largeCounts = [57171, 66238, 112005, 114077, 122158];
        for (var i = 0; i < largeIds.Length; i++)
        {
            readingCounts[largeIds[i]] = largeCounts[i];
        }
        foreach (var id in smallIds)
        {
            readingCounts[id] = 50;
        }
        var allIds = largeIds.Concat(smallIds).ToArray();

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume(allIds, readingCounts, maxReadingsPerChunk: 20_000, maxImportsPerChunk: 200)
            .ToList();

        // Every large import (each individually over the 20,000 threshold) must be alone in its chunk.
        foreach (var largeId in largeIds)
        {
            var chunkContainingIt = chunks.Single(c => c.Contains(largeId));
            chunkContainingIt.ShouldBe([largeId]);
        }
        // No MULTI-import chunk's cumulative reading count exceeds the threshold — a single
        // import alone is allowed to exceed it (the accepted, unsplittable edge case), which is
        // exactly what each of the 5 large imports above does.
        chunks.Where(chunk => chunk.Length > 1).ShouldAllBe(chunk => chunk.Sum(id => readingCounts[id]) <= 20_000);
        // Every id is accounted for exactly once.
        chunks.SelectMany(c => c).OrderBy(id => id).ShouldBe(allIds.OrderBy(id => id));
    }

    [Fact]
    public void A_single_import_alone_over_threshold_still_forms_one_chunk()
    {
        var id = Guid.NewGuid();
        var readingCounts = new Dictionary<Guid, int> { [id] = 500_000 };

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume([id], readingCounts, maxReadingsPerChunk: 20_000, maxImportsPerChunk: 200)
            .ToList();

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBe([id]);
    }

    [Fact]
    public void An_import_missing_from_the_dictionary_is_treated_as_zero_readings()
    {
        var ids = Ids(3);
        var readingCounts = new Dictionary<Guid, int>(); // none present

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume(ids, readingCounts, maxReadingsPerChunk: 20_000, maxImportsPerChunk: 200)
            .ToList();

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBe(ids);
    }

    [Fact]
    public void Small_imports_pack_together_until_the_volume_threshold_would_be_exceeded()
    {
        var ids = Ids(3);
        var readingCounts = new Dictionary<Guid, int> { [ids[0]] = 8_000, [ids[1]] = 8_000, [ids[2]] = 8_000 };

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume(ids, readingCounts, maxReadingsPerChunk: 20_000, maxImportsPerChunk: 200)
            .ToList();

        // 8,000 + 8,000 = 16,000 fits; a third 8,000 would push to 24,000, over threshold — new chunk.
        chunks.Count.ShouldBe(2);
        chunks[0].ShouldBe([ids[0], ids[1]]);
        chunks[1].ShouldBe([ids[2]]);
    }

    [Fact]
    public void Empty_input_yields_no_chunks()
    {
        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume([], new Dictionary<Guid, int>(), maxReadingsPerChunk: 20_000, maxImportsPerChunk: 200)
            .ToList();

        chunks.ShouldBeEmpty();
    }

    [Fact]
    public void Splits_correctly_when_the_count_cap_and_volume_cap_are_both_hit_by_the_same_item()
    {
        // Round-3 review finding (Blind Hunter): the split condition is an OR of the count cap and
        // the volume cap — no existing test exercised a case where adding the next id would exceed
        // BOTH simultaneously, so a future refactor to AND (only splitting when both are exceeded)
        // would silently change behavior without any test catching it.
        var ids = Ids(2);
        var readingCounts = new Dictionary<Guid, int> { [ids[0]] = 10, [ids[1]] = 10 };

        var chunks = SmartPlugImportRepository
            .ChunkImportIdsByReadingVolume(ids, readingCounts, maxReadingsPerChunk: 15, maxImportsPerChunk: 1)
            .ToList();

        // Adding ids[1] to a chunk already holding ids[0] would exceed the count cap (1 already
        // reached) AND the volume cap (10 + 10 = 20 > 15) at once — still must split, not merge.
        chunks.Count.ShouldBe(2);
        chunks[0].ShouldBe([ids[0]]);
        chunks[1].ShouldBe([ids[1]]);
    }
}
