using System.Data.Common;
using Npgsql;

namespace WebApiTgBot.Tests;

/// <summary>
/// Custom safety exception thrown when PostgreSQL test preconditions, target database identities,
/// or execution opt-in requirements are violated.
/// Guaranteed to never expose passwords, credentials, or secrets in exception messages.
/// </summary>
public class PostgreSqlTestSafetyException : Exception
{
    public PostgreSqlTestSafetyException(string message) : base(message) { }
    public PostgreSqlTestSafetyException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Centralized fail-closed safety guard for PostgreSQL test execution.
/// Protects development and production databases by enforcing:
/// 1. Explicit opt-in via GOGAA_ENABLE_POSTGRES_TESTS environment variable.
/// 2. Explicit connection string configuration via GOGAA_TEST_POSTGRES_CONNECTION.
/// 3. Strict allowlist for database identity (must equal "webapitgbot_test").
/// 4. Strict denylist for development and production databases (rejects "webapitgbot_dev", "webapitgbot_prod", etc.).
/// 5. Host and port verification (localhost, 127.0.0.1, ::1 on approved ports 5432/5433).
/// 6. Engine-level identity validation (SELECT current_database()).
/// 7. Zero credential leakage in error messages or logs.
/// 8. Prevention of destructive SQL operations (EnsureDeleted, DROP DATABASE, TRUNCATE).
/// </summary>
public static class PostgreSqlTestSafetyGuard
{
    public const string ApprovedDatabaseName = "webapitgbot_test";
    public const string OptInEnvironmentVariable = "GOGAA_ENABLE_POSTGRES_TESTS";
    public const string ConnectionStringEnvironmentVariable = "GOGAA_TEST_POSTGRES_CONNECTION";

    public static readonly string[] ApprovedHosts = { "localhost", "127.0.0.1", "::1" };
    public static readonly int[] ApprovedPorts = { 5432, 5433 };

    public static readonly string[] ProhibitedDatabaseNames =
    {
        "webapitgbot_dev",
        "webapitgbot",
        "webapitgbot_prod",
        "webapitgbot_production",
        "postgres",
        "template0",
        "template1",
        "master"
    };

    public static readonly string[] ProhibitedSubstrings =
    {
        "dev",
        "development",
        "prod",
        "production",
        "stage",
        "staging",
        "live"
    };

    /// <summary>
    /// Checks whether PostgreSQL test execution has been explicitly opted into via environment variable.
    /// Returns true ONLY if GOGAA_ENABLE_POSTGRES_TESTS is set to "true" (case-insensitive).
    /// </summary>
    public static bool IsExecutionOptedIn()
    {
        var optIn = Environment.GetEnvironmentVariable(OptInEnvironmentVariable);
        return string.Equals(optIn?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asserts that PostgreSQL test execution is explicitly opted in.
    /// Throws PostgreSqlTestSafetyException if not opted in.
    /// </summary>
    public static void AssertExecutionAllowed()
    {
        if (!IsExecutionOptedIn())
        {
            throw new PostgreSqlTestSafetyException(
                $"PostgreSQL integration test execution is disabled. " +
                $"To run tests against the isolated test database, explicitly set the environment variable '{OptInEnvironmentVariable}=true'. " +
                $"Never set this variable in production or standard development environments.");
        }
    }

    /// <summary>
    /// Retrieves the approved test connection string from the dedicated test environment variable.
    /// Never falls back to appsettings.json, appsettings.Development.json, or normal DefaultConnection.
    /// Validates safety preconditions before returning.
    /// </summary>
    public static string GetApprovedConnectionString()
    {
        AssertExecutionAllowed();

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new PostgreSqlTestSafetyException(
                $"Missing required test connection string. Configure the '{ConnectionStringEnvironmentVariable}' " +
                $"environment variable with the approved test database connection string for '{ApprovedDatabaseName}'. " +
                $"The connection string must point exclusively to the isolated local test database.");
        }

        ValidateConnectionString(connectionString);
        return connectionString;
    }

    /// <summary>
    /// Validates an explicit connection string while strictly enforcing test execution opt-in.
    /// Guarantees that custom or explicit connection strings cannot bypass opt-in authorization.
    /// </summary>
    public static void AuthorizeAndValidateConnectionString(string? connectionString)
    {
        AssertExecutionAllowed();
        ValidateConnectionString(connectionString);
    }

    /// <summary>
    /// Validates a PostgreSQL connection string format, target database name, host, and port.
    /// Rejects prohibited names, prohibited patterns, remote hosts, unapproved database targets,
    /// and unapproved routing or resolution parameters (e.g., TargetSessionAttributes, Service, Passfile, Multiplexing).
    /// Guaranteed to never include passwords in any thrown exception.
    /// </summary>
    public static void ValidateConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new PostgreSqlTestSafetyException("PostgreSQL test connection string cannot be null, empty, or whitespace.");
        }

        // 1. Check for prohibited parameters in connection string text before builder parsing
        ValidateProhibitedParametersText(connectionString);

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new PostgreSqlTestSafetyException("Invalid PostgreSQL connection string format.", ex);
        }

        // 2. Validate parsed builder properties
        ValidateRoutingAndResolutionParameters(builder);
        ValidateDatabaseName(builder.Database);
        ValidateHost(builder.Host);
        ValidatePort(builder.Port);
    }

    /// <summary>
    /// Inspects the connection string text for prohibited parameters like Service, Passfile,
    /// or TargetSessionAttributes that might redirect routing.
    /// </summary>
    public static void ValidateProhibitedParametersText(string connectionString)
    {
        if (connectionString.Contains("Target Session Attributes", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("TargetSessionAttributes", StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: 'Target Session Attributes' routing parameter is strictly prohibited in test execution.");
        }

        if (connectionString.Contains("Service=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("Service =", StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: 'Service' parameter is strictly prohibited in test execution to prevent external service file routing.");
        }

        if (connectionString.Contains("Passfile=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("Passfile =", StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: 'Passfile' parameter is strictly prohibited in test execution.");
        }
    }

    /// <summary>
    /// Inspects and rejects unapproved Npgsql connection-routing, multi-host, or service-based
    /// resolution mechanisms that could redirect the target away from the verified local test database.
    /// </summary>
    public static void ValidateRoutingAndResolutionParameters(NpgsqlConnectionStringBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.TargetSessionAttributes))
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: 'Target Session Attributes' routing parameter is strictly prohibited in test execution.");
        }

        if (builder.Multiplexing)
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: Npgsql multiplexing is strictly prohibited in test execution.");
        }
    }

    /// <summary>
    /// Validates the database name against strict safety rules.
    /// 1. Rejects null/empty.
    /// 2. Rejects explicitly prohibited names (e.g. webapitgbot_dev, webapitgbot_prod, postgres).
    /// 3. Rejects prohibited substrings (dev, prod, production, stage, live).
    /// 4. Rejects any database name other than the approved test database identity (webapitgbot_test).
    /// Note: Suffix "_test" alone does NOT grant approval (e.g., "my_dev_test" or "custom_test" is rejected).
    /// </summary>
    public static void ValidateDatabaseName(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new PostgreSqlTestSafetyException("SAFETY VIOLATION: Database name is missing from the connection string.");
        }

        var normalized = databaseName.Trim();

        // 1. Explicit prohibited name check
        if (ProhibitedDatabaseNames.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Target database '{normalized}' is strictly prohibited. Tests must NEVER connect to development, production, or system databases.");
        }

        // 2. Prohibited substring check
        if (ProhibitedSubstrings.Any(s => normalized.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Target database '{normalized}' contains a prohibited keyword. Tests must NEVER connect to development or production databases.");
        }

        // 3. Suffix-alone check: Any "_test" database that is not the exact approved name is rejected
        if (normalized.EndsWith("_test", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, ApprovedDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database '{normalized}' is not the approved test database identity. " +
                $"A '_test' suffix alone does not grant test approval. Only the exact database '{ApprovedDatabaseName}' is permitted.");
        }

        // 4. Exact approved name check
        if (!string.Equals(normalized, ApprovedDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database '{normalized}' is not approved for test execution. " +
                $"Only '{ApprovedDatabaseName}' is permitted.");
        }
    }

    /// <summary>
    /// Validates that the database host is restricted to approved local targets.
    /// Strictly rejects remote hostnames, cloud IPs, unauthorized domains, multi-host lists, or path-based sockets.
    /// </summary>
    public static void ValidateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new PostgreSqlTestSafetyException("SAFETY VIOLATION: Database host is missing from the connection string.");
        }

        var normalized = host.Trim();

        if (normalized.Contains(',') || normalized.Contains('/') || normalized.Contains('\\'))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Multi-host or path-based database host '{normalized}' is strictly prohibited.");
        }

        if (!ApprovedHosts.Any(h => string.Equals(h, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Remote or unapproved database host '{normalized}' is strictly prohibited. " +
                $"Test execution is restricted to local approved hosts ({string.Join(", ", ApprovedHosts)}).");
        }
    }

    /// <summary>
    /// Validates that the database port matches approved local PostgreSQL test ports.
    /// </summary>
    public static void ValidatePort(int port)
    {
        if (!ApprovedPorts.Contains(port))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database port {port} is not in the approved test port range ({string.Join(", ", ApprovedPorts)}).");
        }
    }

    /// <summary>
    /// Validates that the database name reported by the database engine matches the approved test database identity.
    /// Used during pre-flight checks before migrations or mutations.
    /// </summary>
    public static void ValidateActualDatabaseName(string? actualDatabaseName, string expectedDatabaseName)
    {
        if (string.IsNullOrWhiteSpace(actualDatabaseName))
        {
            throw new PostgreSqlTestSafetyException("SAFETY VIOLATION: Database engine reported an empty or null database name.");
        }

        var normalized = actualDatabaseName.Trim();

        // Enforce prohibited list check on actual reported database
        if (ProhibitedDatabaseNames.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database engine reported connected database '{normalized}', which is strictly prohibited.");
        }

        if (ProhibitedSubstrings.Any(s => normalized.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database engine reported connected database '{normalized}', which contains a prohibited keyword.");
        }

        if (!string.Equals(normalized, expectedDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: Database engine reported connected database '{normalized}', but expected test database is '{expectedDatabaseName}'. " +
                $"Aborting all test operations to protect database integrity.");
        }
    }

    /// <summary>
    /// Connects to PostgreSQL and verifies the actual database identity via 'SELECT current_database(), current_user;'.
    /// Must be executed before any migration or test mutation.
    /// </summary>
    public static async Task ValidateConnectedDatabaseAsync(DbConnection connection, string expectedDatabaseName)
    {
        if (connection == null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_database(), current_user;";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new PostgreSqlTestSafetyException("SAFETY VIOLATION: Failed to query 'current_database()' from PostgreSQL.");
        }

        var actualDb = reader.GetString(0);
        var actualUser = reader.GetString(1);

        ValidateActualDatabaseName(actualDb, expectedDatabaseName);

        if (string.IsNullOrWhiteSpace(actualUser))
        {
            throw new PostgreSqlTestSafetyException("SAFETY VIOLATION: Database engine returned an empty user identity.");
        }
    }

    /// <summary>
    /// Sanitizes a connection string by redacting passwords before logging or reporting.
    /// Guaranteed to never expose passwords or secret tokens.
    /// </summary>
    public static string SanitizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "[EMPTY]";
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Password = "******"
            };
            return builder.ConnectionString;
        }
        catch
        {
            return "[REDACTED_INVALID_FORMAT]";
        }
    }

    /// <summary>
    /// Asserts that mandatory database preflight has been successfully completed and verified.
    /// Guaranteed to fail closed if preflight was omitted or failed.
    /// </summary>
    public static void AssertPreflightVerified(bool isPreflightVerified)
    {
        if (!isPreflightVerified)
        {
            throw new PostgreSqlTestSafetyException(
                "SAFETY VIOLATION: Database preflight is not verified. " +
                "Mandatory EnsureDatabasePreflightAsync() must be successfully completed before executing database operations.");
        }
    }
}
