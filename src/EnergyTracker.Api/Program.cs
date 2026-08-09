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
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=energytracker;Username=postgres;Password=postgres";

builder.Services.AddDbContext<EnergyTrackerDbContext>(options =>
{
    switch (databaseProvider)
    {
        case "Postgres":
            options.UseNpgsql(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
            break;
        case "SqlServer":
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
