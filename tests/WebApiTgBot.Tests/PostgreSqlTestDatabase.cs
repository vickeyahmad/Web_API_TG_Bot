using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;

namespace WebApiTgBot.Tests;

/// <summary>
/// Centralized helper for managing the isolated PostgreSQL test database (webapitgbot_test).
/// Integrates with PostgreSqlTestSafetyGuard to enforce:
/// 1. Explicit opt-in via GOGAA_ENABLE_POSTGRES_TESTS=true.
/// 2. Connection string configured exclusively via GOGAA_TEST_POSTGRES_CONNECTION.
/// 3. Pre-flight engine verification (SELECT current_database()).
/// 4. Zero credentials hardcoded in source code or tracked files.
/// 5. Fail-fast error reporting instead of silent test skips.
/// </summary>
public static class PostgreSqlTestDatabase
{
    private static readonly SemaphoreSlim _migrationLock = new(1, 1);
    private static bool _isMigrated = false;

    /// <summary>
    /// Gets the approved test connection string through the safety guard.
    /// Throws PostgreSqlTestSafetyException if opt-in is missing, connection string is missing,
    /// or safety validation fails.
    /// </summary>
    public static string ConnectionString => GetConnectionString();

    /// <summary>
    /// Retrieves the approved test connection string via PostgreSqlTestSafetyGuard.
    /// </summary>
    public static string GetConnectionString()
    {
        return PostgreSqlTestSafetyGuard.GetApprovedConnectionString();
    }

    /// <summary>
    /// Creates DbContextOptions configured with the approved, validated test connection string.
    /// </summary>
    public static DbContextOptions<AppDbContext> CreateOptions()
    {
        var connectionString = GetConnectionString();
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    /// <summary>
    /// Checks connectivity to the isolated PostgreSQL test database, validates engine identity
    /// (SELECT current_database()), and applies all pending EF Core migrations before test execution.
    /// Thread-safe and non-destructive.
    /// Throws PostgreSqlTestSafetyException or InvalidOperationException if preconditions fail
    /// (never silently skips execution).
    /// </summary>
    public static async Task<bool> EnsureMigratedAsync(AppDbContext db)
    {
        // 1. Fail-fast validation of opt-in and connection string
        PostgreSqlTestSafetyGuard.AssertExecutionAllowed();
        var connectionString = GetConnectionString();
        PostgreSqlTestSafetyGuard.ValidateConnectionString(connectionString);

        // 2. Connectivity check
        if (!await db.Database.CanConnectAsync())
        {
            throw new InvalidOperationException(
                $"PostgreSQL test database '{PostgreSqlTestSafetyGuard.ApprovedDatabaseName}' is unreachable. " +
                $"Ensure the isolated local test PostgreSQL instance is running on port {PostgreSqlTestSafetyGuard.ApprovedPort} and accepting connections.");
        }

        // 3. Engine-level identity validation (SELECT current_database(), current_user)
        var rawConn = db.Database.GetDbConnection();
        await PostgreSqlTestSafetyGuard.ValidateConnectedDatabaseAsync(
            rawConn,
            PostgreSqlTestSafetyGuard.ApprovedDatabaseName,
            PostgreSqlTestSafetyGuard.ApprovedDatabaseUser);

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

    /// <summary>
    /// Indicates whether EF Core migrations have been verified and applied to the test database.
    /// </summary>
    public static bool IsMigrated => _isMigrated;

    /// <summary>
    /// Asserts that the test database has completed preflight validation and migration.
    /// Guards direct fixture inserts and raw DbContext test setup.
    /// </summary>
    public static void AssertMigrated()
    {
        PostgreSqlTestSafetyGuard.AssertExecutionAllowed();
        if (!_isMigrated)
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: Test database has not completed preflight migration. " +
                "PostgreSqlTestDatabase.EnsureMigratedAsync() must be successfully completed before executing database operations.");
        }
    }

    /// <summary>
    /// Resets the migration lock state for testing purposes.
    /// </summary>
    public static void ResetMigrationStateForTesting()
    {
        _isMigrated = false;
    }
}
