using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Database:Provider is read exactly once, here at the composition root (Consistency Conventions) —
// nothing in Infrastructure re-reads or branches on it independently.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
// Matches docker-compose.yml's default POSTGRES_USER/POSTGRES_DB and .env.example's default
// POSTGRES_PASSWORD, so `dotnet run` against `docker compose up postgres -d` works with no
// extra configuration as long as .env's password wasn't changed from the example.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=energytracker;Username=energytracker;Password=change-me";

builder.Services.AddDbContext<EnergyTrackerDbContext>(options =>
{
    switch (databaseProvider.ToLowerInvariant())
    {
        case "postgres":
            options.UseNpgsql(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
            break;
        case "sqlserver":
            options.UseSqlServer(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{databaseProvider}'. Expected 'Postgres' or 'SqlServer'.");
    }
});

var app = builder.Build();

// Liveness only — no DB/dependency check (AD-19): a slow Postgres/Azure SQL must never fail this probe.
app.MapGet("/health", () => Results.Ok());

// Single-artifact deployment (AD-13): the API serves the built React SPA from wwwroot/.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
