using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.MsSql;

namespace EnergyTracker.Infrastructure.Tests;

// Regression coverage for the 2026-09-05 production incident (energy-tracker-rg): a second Eve
// Home smart-plug import for an already-mapped device failed import with the generic
// "An unexpected error occurred while processing this import." message. Root cause:
// infra/sql/grant-entra-db-users.sql deliberately provisions the Container App's runtime identity
// with only db_datareader/db_datawriter ("no schema-change rights") — but
// EFCore.BulkExtensions.SqlServer's BulkInsertOrUpdateAsync (AD-23's mapped-PowerPoint write path)
// stages its MERGE through a table it creates itself via `SELECT ... INTO [dbo].[<name>Temp...]`,
// a DDL operation that needs CREATE TABLE, and throws "CREATE TABLE permission denied" without it.
// Every other SmartPlugImportRepository SQL Server test authenticates as `sa` (db_owner), so this
// gap never surfaced until it hit production. This test authenticates as a login carrying exactly
// AD-21's runtime grant to reproduce the failure and to prove the fix resolves it.
public class SmartPlugImportRepositoryAddAsyncMinimalSqlServerPermissionsTests : IAsyncLifetime
{
    private const string RuntimeLogin = "runtime_identity_test_login";
    private const string RuntimeLoginPassword = "R3stricted!Runtime#2026";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Migrate the schema as the container's admin login, then provision a second login
        // mirroring infra/sql/grant-entra-db-users.sql's runtime grant exactly — db_datareader +
        // db_datawriter only, deliberately no db_ddladmin.
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(_container.GetConnectionString(),
            o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
        await using (var migratingDbContext = new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(null)))
        {
            await migratingDbContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var provisionResult = await _container.ExecScriptAsync($"""
            CREATE LOGIN [{RuntimeLogin}] WITH PASSWORD = '{RuntimeLoginPassword}';
            CREATE USER [{RuntimeLogin}] FOR LOGIN [{RuntimeLogin}];
            ALTER ROLE db_datareader ADD MEMBER [{RuntimeLogin}];
            ALTER ROLE db_datawriter ADD MEMBER [{RuntimeLogin}];
            """, TestContext.Current.CancellationToken);
        provisionResult.ExitCode.ShouldBe(0L, provisionResult.Stderr);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class FixedHouseholdAccessor(Guid? householdId) : ICurrentHouseholdAccessor
    {
        public Guid? HouseholdId { get; } = householdId;

        public Guid? HouseholdMemberId => null;
    }

    private EnergyTrackerDbContext OpenDbContextAsRuntimeLogin(Guid householdId)
    {
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            UserID = RuntimeLogin,
            Password = RuntimeLoginPassword,
        };
        var optionsBuilder = new DbContextOptionsBuilder<EnergyTrackerDbContext>();
        optionsBuilder.UseSqlServer(builder.ConnectionString);
        return new EnergyTrackerDbContext(optionsBuilder.Options, new FixedHouseholdAccessor(householdId));
    }

    [Fact]
    public async Task AddAsync_upserts_a_mapped_PowerPoint_batch_under_the_runtime_identitys_minimal_grant()
    {
        var householdId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Guid powerPointId;
        Guid backgroundJobId;
        await using (var seedDbContext = OpenDbContextAsRuntimeLogin(householdId))
        {
            seedDbContext.Households.Add(new Household { Id = householdId, Locale = "en-US", Currency = "USD", CreatedAtUtc = now });
            var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = now };
            seedDbContext.Rooms.Add(room);
            var powerPoint = new PowerPoint { Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Fridge", CreatedAtUtc = now };
            seedDbContext.PowerPoints.Add(powerPoint);
            var backgroundJob = new BackgroundJob
            {
                Id = Guid.NewGuid(),
                HouseholdId = householdId,
                JobType = "ProcessSmartPlugImport",
                Status = BackgroundJobStatus.Processing,
                CreatedAtUtc = now,
            };
            seedDbContext.BackgroundJobs.Add(backgroundJob);
            await seedDbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            powerPointId = powerPoint.Id;
            backgroundJobId = backgroundJob.Id;
        }

        await using var dbContext = OpenDbContextAsRuntimeLogin(householdId);
        var repository = new SmartPlugImportRepository(dbContext, new AuditCorrectionRecorder(dbContext), NullLogger<SmartPlugImportRepository>.Instance);
        var import = new SmartPlugImport
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            BackgroundJobId = backgroundJobId,
            VendorFormat = SmartPlugVendorFormat.EveHome,
            OriginalFileName = "2026-07-13_Netzwerk_Gesamtverbrauch.xlsx",
            Status = SmartPlugImportStatus.Completed,
            DeviceTag = "Fridge",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
        };
        var reading = new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            SmartPlugImportId = import.Id,
            PowerPointId = powerPointId,
            RoomName = "Kitchen",
            PowerPointName = "Fridge",
            DeviceName = "Fridge",
            IntervalStart = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            IntervalEnd = new DateTimeOffset(2026, 7, 13, 0, 15, 0, TimeSpan.Zero),
            KwhValue = 0.42m,
        };

        // Before the fix, this throws Microsoft.Data.SqlClient.SqlException: "CREATE TABLE
        // permission denied in database ..." — BulkInsertOrUpdateAsync stages its MERGE through a
        // permanent table it creates itself, which this login (mirroring the real runtime
        // identity's grant) is deliberately not allowed to do.
        await repository.AddAsync(import, [reading], TestContext.Current.CancellationToken);

        await using var verifyDbContext = OpenDbContextAsRuntimeLogin(householdId);
        var persisted = await verifyDbContext.SmartPlugReadings
            .SingleAsync(r => r.SmartPlugImportId == import.Id, TestContext.Current.CancellationToken);
        persisted.KwhValue.ShouldBe(0.42m);
    }
}
