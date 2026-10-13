using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Infrastructure.Data;

namespace WebApiTgBot.Tests;

/// <summary>
/// Centralized helper for managing the isolated PostgreSQL test database (webapitgbot_test).
/// Ensures EF Core migrations are applied safely and idempotently before integration tests execute.
/// </summary>
public static class PostgreSqlTestDatabase
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=webapitgbot_test;Username=webapitgbot;Password=Dev_Local_Password_2024!";

    private static readonly SemaphoreSlim _migrationLock = new(1, 1);
    private static bool _isMigrated = false;

    public static DbContextOptions<AppDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
    }

    /// <summary>
    /// Checks connectivity to the isolated PostgreSQL test database and safely applies
    /// all pending EF Core migrations before test execution. Thread-safe and non-destructive.
    /// </summary>
    /// <returns>True if connected and migrated successfully; false if PostgreSQL is unreachable.</returns>
    public static async Task<bool> EnsureMigratedAsync(AppDbContext db)
    {
        if (!await db.Database.CanConnectAsync())
        {
            return false;
        }

        if (_isMigrated)
        {
            return true;
        }

        await _migrationLock.WaitAsync();
        try
        {
            if (!_isMigrated)
            {
                await db.Database.MigrateAsync();
                _isMigrated = true;
            }
            return true;
        }
        finally
        {
            _migrationLock.Release();
        }
    }
}
