using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Real temp files, no mocks (Story 11.10, audit S4): the registry's whole job is to own a file's
// lifetime, so the assertions look at the disk, not at a substitute.
public sealed class MemoryHouseholdImportUploadRegistryTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"registry-tests-{Guid.NewGuid()}")).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private HouseholdImportUploadReference NewUpload(Guid householdId)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{}");
        return new HouseholdImportUploadReference(householdId, path, "export.json");
    }

    private string[] FilesOnDisk() => Directory.GetFiles(_directory);

    [Fact]
    public void Consume_returns_the_reference_for_the_owner_and_a_second_Consume_is_null()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var upload = NewUpload(household);
        var token = Guid.NewGuid();
        registry.Register(token, upload);

        registry.Consume(token, household).ShouldBe(upload);
        registry.Consume(token, household).ShouldBeNull();
    }

    [Fact]
    public void A_consumed_uploads_file_is_left_alone_it_now_belongs_to_the_restore_job()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var upload = NewUpload(household);
        var token = Guid.NewGuid();
        registry.Register(token, upload);

        registry.Consume(token, household).ShouldNotBeNull();

        File.Exists(upload.TempFilePath).ShouldBeTrue();
    }

    [Fact]
    public void Consume_for_another_Household_returns_null_and_deletes_the_file()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var upload = NewUpload(Guid.NewGuid());
        var token = Guid.NewGuid();
        registry.Register(token, upload);

        registry.Consume(token, Guid.NewGuid()).ShouldBeNull();

        File.Exists(upload.TempFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Consume_of_an_expired_entry_returns_null_and_deletes_the_file()
    {
        var registry = new MemoryHouseholdImportUploadRegistry(TimeSpan.FromMilliseconds(50));
        var household = Guid.NewGuid();
        var upload = NewUpload(household);
        var token = Guid.NewGuid();
        registry.Register(token, upload);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        registry.Consume(token, household).ShouldBeNull();

        File.Exists(upload.TempFilePath).ShouldBeFalse();
    }

    [Fact]
    public void Consume_of_an_unknown_token_returns_null_and_touches_no_file()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var upload = NewUpload(household);
        registry.Register(Guid.NewGuid(), upload);

        registry.Consume(Guid.NewGuid(), household).ShouldBeNull();

        File.Exists(upload.TempFilePath).ShouldBeTrue();
    }

    [Fact]
    public void Register_for_a_Household_with_a_pending_upload_replaces_it_and_deletes_the_old_file()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var first = NewUpload(household);
        var second = NewUpload(household);
        var firstToken = Guid.NewGuid();
        var secondToken = Guid.NewGuid();
        registry.Register(firstToken, first);

        registry.Register(secondToken, second);

        File.Exists(first.TempFilePath).ShouldBeFalse();
        File.Exists(second.TempFilePath).ShouldBeTrue();
        registry.Consume(firstToken, household).ShouldBeNull();
        registry.Consume(secondToken, household).ShouldBe(second);
    }

    [Fact]
    public void Two_Households_each_keep_their_own_pending_upload_and_file()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var householdA = Guid.NewGuid();
        var householdB = Guid.NewGuid();
        var uploadA = NewUpload(householdA);
        var uploadB = NewUpload(householdB);
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        registry.Register(tokenA, uploadA);
        registry.Register(tokenB, uploadB);

        FilesOnDisk().Length.ShouldBe(2);
        registry.Consume(tokenA, householdA).ShouldBe(uploadA);
        registry.Consume(tokenB, householdB).ShouldBe(uploadB);
    }

    [Fact]
    public void A_second_upload_by_one_Household_never_touches_another_Households_entry()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var other = NewUpload(Guid.NewGuid());
        registry.Register(Guid.NewGuid(), other);
        var household = Guid.NewGuid();

        registry.Register(Guid.NewGuid(), NewUpload(household));
        registry.Register(Guid.NewGuid(), NewUpload(household));

        File.Exists(other.TempFilePath).ShouldBeTrue();
    }

    // AC #9b: a token that was already consumed belongs to a queued or running restore job; the
    // one-pending-upload rule must never reach into it.
    [Fact]
    public void Registering_again_never_deletes_the_file_of_an_already_consumed_upload()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var running = NewUpload(household);
        var runningToken = Guid.NewGuid();
        registry.Register(runningToken, running);
        registry.Consume(runningToken, household).ShouldNotBeNull();

        registry.Register(Guid.NewGuid(), NewUpload(household));

        File.Exists(running.TempFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Register_still_sweeps_other_expired_entries_and_deletes_their_files()
    {
        var registry = new MemoryHouseholdImportUploadRegistry(TimeSpan.FromMilliseconds(100));
        var abandoned = NewUpload(Guid.NewGuid());
        registry.Register(Guid.NewGuid(), abandoned);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        registry.Register(Guid.NewGuid(), NewUpload(Guid.NewGuid()));

        File.Exists(abandoned.TempFilePath).ShouldBeFalse();
    }

    // AC #9c: ConcurrentDictionary alone cannot make "remove this Household's old entry and add the
    // new one" atomic. Fifty racing uploads must end with one entry and one file, whoever wins.
    [Fact]
    public async Task Fifty_concurrent_Registers_for_one_Household_leave_exactly_one_entry_and_one_file()
    {
        for (var round = 0; round < 5; round++)
        {
            var registry = new MemoryHouseholdImportUploadRegistry();
            var household = Guid.NewGuid();
            var uploads = Enumerable.Range(0, 50).Select(_ => (Token: Guid.NewGuid(), Reference: NewUpload(household))).ToList();
            using var gate = new ManualResetEventSlim(false);

            var tasks = uploads.Select(u => Task.Run(() =>
            {
                gate.Wait();
                registry.Register(u.Token, u.Reference);
            }, TestContext.Current.CancellationToken)).ToList();
            gate.Set();
            await Task.WhenAll(tasks);

            var survivors = uploads.Where(u => registry.Consume(u.Token, household) is not null).ToList();
            survivors.Count.ShouldBe(1);
            FilesOnDisk().ShouldBe([survivors.Single().Reference.TempFilePath]);

            File.Delete(survivors.Single().Reference.TempFilePath);
        }
    }

    [Fact]
    public void A_file_that_is_already_gone_never_throws_out_of_Register_or_Consume()
    {
        var registry = new MemoryHouseholdImportUploadRegistry();
        var household = Guid.NewGuid();
        var first = NewUpload(household);
        var firstToken = Guid.NewGuid();
        registry.Register(firstToken, first);
        File.Delete(first.TempFilePath);

        Should.NotThrow(() => registry.Register(Guid.NewGuid(), NewUpload(household)));

        var foreign = NewUpload(Guid.NewGuid());
        var foreignToken = Guid.NewGuid();
        registry.Register(foreignToken, foreign);
        File.Delete(foreign.TempFilePath);
        Should.NotThrow(() => registry.Consume(foreignToken, Guid.NewGuid()));
    }
}
