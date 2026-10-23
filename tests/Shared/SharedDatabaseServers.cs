using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(EnergyTracker.TestSupport.SharedDatabaseServers))]

namespace EnergyTracker.TestSupport;

// Linked into EnergyTracker.Infrastructure.Tests and EnergyTracker.Api.Tests: one copy of the source, one fixture
// and one server pair per test assembly. One Postgres and one SQL Server container per test run, started on first
// use. Every test gets its own empty database on them (PostgresTestDatabase / SqlServerTestDatabase), so isolation
// stays per test while the container start (seconds for Postgres, much longer for SQL Server) is paid once instead
// of once per test method. The assembly fixture only exists to dispose the servers at the end of the run.
public sealed class SharedDatabaseServers : IAsyncLifetime
{
    private const string PostgresImage = "postgres:18-alpine";
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-latest";

    // A first start can fail transiently under load (SQL Server exited with CoInitializeSecurity on 2026-09-13);
    // with one shared container that would fail every test, so a few attempts with a fresh container.
    private const int StartAttempts = 3;

    private static readonly Lazy<Task<PostgreSqlContainer>> Postgres =
        new(() => StartWithRetryAsync(() => new PostgreSqlBuilder(PostgresImage).Build()));

    private static readonly Lazy<Task<MsSqlContainer>> SqlServer =
        new(() => StartWithRetryAsync(() => new MsSqlBuilder(SqlServerImage).Build()));

    internal static Task<PostgreSqlContainer> PostgresAsync() => Postgres.Value;

    internal static Task<MsSqlContainer> SqlServerAsync() => SqlServer.Value;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Postgres.IsValueCreated)
        {
            await DisposeStartedAsync(Postgres.Value);
        }

        if (SqlServer.IsValueCreated)
        {
            await DisposeStartedAsync(SqlServer.Value);
        }
    }

    private static async Task DisposeStartedAsync<TContainer>(Task<TContainer> started)
        where TContainer : IAsyncDisposable
    {
        // A start that never succeeded has nothing to dispose; the failure already surfaced in the tests.
        if (started.IsCompletedSuccessfully)
        {
            await started.Result.DisposeAsync();
        }
    }

    private static async Task<TContainer> StartWithRetryAsync<TContainer>(Func<TContainer> create)
        where TContainer : IContainer
    {
        for (var attempt = 1; ; attempt++)
        {
            var container = create();
            try
            {
                await container.StartAsync();
                return container;
            }
            catch when (attempt < StartAttempts)
            {
                await container.DisposeAsync();
            }
            catch
            {
                // Last attempt: the original failure is what the tests should report, not a cleanup error.
                try
                {
                    await container.DisposeAsync();
                }
                catch
                {
                }

                throw;
            }
        }
    }
}

// Same surface the tests used on PostgreSqlContainer (StartAsync, GetConnectionString, DisposeAsync), but
// StartAsync creates a fresh database on the shared server and DisposeAsync drops it.
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    // Concurrent CREATE DATABASE calls copy template1 and can collide ("source database is being accessed by
    // other users"); creating is fast, so one at a time.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    private readonly string _name = "t_" + Guid.NewGuid().ToString("N");
    private string? _connectionString;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var server = await SharedDatabaseServers.PostgresAsync();
        var admin = new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Pooling = false };

        await CreateGate.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync(admin.ConnectionString, $"CREATE DATABASE \"{_name}\"", cancellationToken);
        }
        finally
        {
            CreateGate.Release();
        }

        admin.Database = _name;
        admin.Pooling = true;
        _connectionString = admin.ConnectionString;
    }

    public string GetConnectionString() =>
        _connectionString ?? throw new InvalidOperationException("StartAsync has not completed.");

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            using (var pooled = new NpgsqlConnection(_connectionString))
            {
                NpgsqlConnection.ClearPool(pooled);
            }

            var server = await SharedDatabaseServers.PostgresAsync();
            var admin = new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Pooling = false };
            await ExecuteAsync(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", CancellationToken.None);
        }
        catch
        {
            // Best effort: the whole server is thrown away at the end of the run, so a failed drop must not
            // fail a test that already passed.
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

// SQL Server counterpart of PostgresTestDatabase. The server's own admin login (sa) owns every database.
public sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private readonly string _name = "t_" + Guid.NewGuid().ToString("N");
    private string? _connectionString;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var server = await SharedDatabaseServers.SqlServerAsync();
        var admin = new SqlConnectionStringBuilder(server.GetConnectionString()) { Pooling = false };

        await ExecuteAsync(admin.ConnectionString, $"CREATE DATABASE [{_name}]", cancellationToken);

        admin.InitialCatalog = _name;
        admin.Pooling = true;
        _connectionString = admin.ConnectionString;
    }

    public string GetConnectionString() =>
        _connectionString ?? throw new InvalidOperationException("StartAsync has not completed.");

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        try
        {
            using (var pooled = new SqlConnection(_connectionString))
            {
                SqlConnection.ClearPool(pooled);
            }

            var server = await SharedDatabaseServers.SqlServerAsync();
            var admin = new SqlConnectionStringBuilder(server.GetConnectionString()) { Pooling = false };
            await ExecuteAsync(
                admin.ConnectionString,
                $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];",
                CancellationToken.None);
        }
        catch
        {
            // Best effort, as for Postgres: the server is discarded with the run.
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
