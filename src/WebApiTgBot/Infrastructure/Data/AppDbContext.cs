using Microsoft.EntityFrameworkCore;

namespace WebApiTgBot.Infrastructure.Data;

/// <summary>
/// The central database context for the application.
/// All database tables are registered here.
/// Modules add their entities via the OnModelCreating method
/// or via separate IEntityTypeConfiguration classes.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly automatically.
        // When new modules add entity configurations, they are picked up here.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
