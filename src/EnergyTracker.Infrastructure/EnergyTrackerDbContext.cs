using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure;

public class EnergyTrackerDbContext(DbContextOptions<EnergyTrackerDbContext> options) : DbContext(options);
