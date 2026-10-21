using WebApiTgBot.Modules.Fulfillments.Options;
using Xunit;

namespace WebApiTgBot.Tests;

/// <summary>
/// Pure safety unit tests for Milestone 6B-6E3B1.
/// These tests verify database protection rules, configuration guards, and fail-closed policies.
/// ZERO PostgreSQL connections, ZERO network calls, and ZERO database mutations are performed.
/// </summary>
public class PostgreSqlTestSafetyGuardTests
{
    // 1. Development DB name rejected
    [Fact]
    public void DevelopmentDatabaseName_IsStrictlyRejected()
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateDatabaseName("webapitgbot_dev"));

        Assert.Contains("strictly prohibited", ex.Message);
        Assert.Contains("webapitgbot_dev", ex.Message);
    }

    // 2. Production-like DB names rejected
    [Theory]
    [InlineData("webapitgbot_prod")]
    [InlineData("webapitgbot_production")]
    [InlineData("production_db")]
    [InlineData("my_prod_app")]
    [InlineData("live_database")]
    [InlineData("staging_store")]
    [InlineData("postgres")]
    [InlineData("master")]
    public void ProductionLikeDatabaseNames_AreStrictlyRejected(string prohibitedDbName)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateDatabaseName(prohibitedDbName));

        Assert.Contains("SAFETY VIOLATION", ex.Message);
    }

    // 3. Missing connection string rejected
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingConnectionString_IsStrictlyRejected(string? emptyConnectionString)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(emptyConnectionString));

        Assert.Contains("cannot be null, empty, or whitespace", ex.Message);
    }

    // 4. Missing execution opt-in rejected
    [Fact]
    public void MissingExecutionOptIn_ThrowsSafetyException()
    {
        // Explicitly clear opt-in environment variable for this test
        var original = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);

            Assert.False(PostgreSqlTestSafetyGuard.IsExecutionOptedIn());

            var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
                PostgreSqlTestSafetyGuard.AssertExecutionAllowed());

            Assert.Contains(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, ex.Message);
            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, original);
        }
    }

    // 5. Unknown host or port rejected
    [Fact]
    public void RemoteDatabaseHost_IsStrictlyRejected()
    {
        var remoteConnStr = "Host=remote.rds.amazonaws.com;Port=5432;Database=webapitgbot_test;Username=testuser;Password=SamplePassword123!";

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(remoteConnStr));

        Assert.Contains("Remote or unapproved database host", ex.Message);
        Assert.Contains("remote.rds.amazonaws.com", ex.Message);
    }

    [Fact]
    public void UnapprovedDatabasePort_IsStrictlyRejected()
    {
        var badPortConnStr = "Host=localhost;Port=9999;Database=webapitgbot_test;Username=testuser;Password=SamplePassword123!";

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(badPortConnStr));

        Assert.Contains("Database port 9999 is not in the approved test port range", ex.Message);
    }

    // 6. Approved test database identity accepted in configuration validation
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=webapitgbot_test;Username=testuser;Password=SamplePassword123!")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=webapitgbot_test;Username=testuser;Password=SamplePassword123!")]
    [InlineData("Host=::1;Port=5432;Database=webapitgbot_test;Username=testuser;Password=SamplePassword123!")]
    public void ApprovedTestDatabaseIdentity_IsAccepted(string approvedConnStr)
    {
        // Must succeed without throwing
        PostgreSqlTestSafetyGuard.ValidateConnectionString(approvedConnStr);
    }

    // 7. Database name suffix alone does not grant approval
    [Theory]
    [InlineData("custom_test")]
    [InlineData("app_test")]
    [InlineData("arbitrary_db_test")]
    [InlineData("my_isolated_test")]
    public void DatabaseNameSuffix_Alone_DoesNotGrantApproval(string unapprovedTestNamedDb)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateDatabaseName(unapprovedTestNamedDb));

        Assert.Contains("suffix alone does not grant test approval", ex.Message);
        Assert.Contains(PostgreSqlTestSafetyGuard.ApprovedDatabaseName, ex.Message);
    }

    // 8. Unexpected actual database identity rejected by the comparison logic
    [Theory]
    [InlineData("webapitgbot_dev")]
    [InlineData("webapitgbot_prod")]
    [InlineData("postgres")]
    [InlineData("unrelated_database")]
    public void UnexpectedActualDatabaseIdentity_RejectedByComparisonLogic(string actualReportedDb)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateActualDatabaseName(actualReportedDb, PostgreSqlTestSafetyGuard.ApprovedDatabaseName));

        Assert.Contains("SAFETY VIOLATION", ex.Message);
    }

    [Fact]
    public void ExpectedActualDatabaseIdentity_MatchesSuccessfully()
    {
        // Validating the exact approved database identity must succeed without exception
        PostgreSqlTestSafetyGuard.ValidateActualDatabaseName("webapitgbot_test", PostgreSqlTestSafetyGuard.ApprovedDatabaseName);
        PostgreSqlTestSafetyGuard.ValidateActualDatabaseName("WEBAPITGBOT_TEST", PostgreSqlTestSafetyGuard.ApprovedDatabaseName);
    }

    // 9. Credentials never appear in safe error messages
    [Fact]
    public void Credentials_NeverAppearInSafeErrorMessages()
    {
        const string secretPassword = "SuperConfidentialPassword987!";
        var invalidConnStr = $"Host=localhost;Port=5432;Database=webapitgbot_dev;Username=testuser;Password={secretPassword}";

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(invalidConnStr));

        // Assert error message NEVER leaks the password
        Assert.DoesNotContain(secretPassword, ex.Message);

        // Assert sanitized string replaces password with asterisks
        var sanitized = PostgreSqlTestSafetyGuard.SanitizeConnectionString(invalidConnStr);
        Assert.DoesNotContain(secretPassword, sanitized);
        Assert.Contains("******", sanitized);
    }

    // 10. Test host does not fall back to normal development configuration
    [Fact]
    public void TestHost_DoesNotFallBackToNormalDevelopmentConfiguration()
    {
        var originalOptIn = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        var originalConn = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.ConnectionStringEnvironmentVariable);

        try
        {
            // Clear environment variables
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.ConnectionStringEnvironmentVariable, null);

            // Attempting to retrieve connection string must fail-closed rather than falling back
            var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
                PostgreSqlTestSafetyGuard.GetApprovedConnectionString());

            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, originalOptIn);
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.ConnectionStringEnvironmentVariable, originalConn);
        }
    }

    // 11. Feature settings remain disabled outside test-only overrides
    [Fact]
    public void FeatureSettings_RemainDisabledOutsideTestOnlyOverrides()
    {
        var defaultOptions = new ManualFulfillmentOptions();

        Assert.False(defaultOptions.Enabled);
        Assert.NotNull(defaultOptions.ApprovedProductSkus);
        Assert.Empty(defaultOptions.ApprovedProductSkus);
        Assert.False(defaultOptions.IsEffectivelyAvailable);
        Assert.False(defaultOptions.IsProductApproved("VAL-VP-1000"));
        Assert.False(defaultOptions.IsProductApproved("ANY-SKU"));
    }

    // 12. Existing in-memory test infrastructure remains independent of PostgreSQL
    [Fact]
    public void ExistingInMemoryTestInfrastructure_RemainsIndependentOfPostgreSql()
    {
        // Instantiating CustomWebApplicationFactory must succeed without requiring PostgreSQL
        // or any PostgreSQL environment variables
        using var factory = new CustomWebApplicationFactory();
        Assert.NotNull(factory);
    }

    // 13. Regression: AuthorizeAndValidateConnectionString strictly requires opt-in
    [Fact]
    public void AuthorizeAndValidateConnectionString_WhenNotOptedIn_ThrowsSafetyException()
    {
        var original = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);

            var validConnStr = "Host=localhost;Port=5432;Database=webapitgbot_test;Username=u;Password=p";
            var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
                PostgreSqlTestSafetyGuard.AuthorizeAndValidateConnectionString(validConnStr));

            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, original);
        }
    }

    // 14. Regression: PostgreSqlWebApplicationFactory constructor has no public custom connection string parameter
    [Fact]
    public void PostgreSqlWebApplicationFactory_HasNoPublicCustomConnectionStringConstructor()
    {
        var constructors = typeof(PostgreSqlWebApplicationFactory).GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
    }

    // 15. Regression: PostgreSqlWebApplicationFactory preflight strictly requires opt-in
    [Fact]
    public async Task PostgreSqlWebApplicationFactory_EnsureDatabasePreflightAsync_WhenNotOptedIn_ThrowsSafetyException()
    {
        var original = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);

            using var factory = new PostgreSqlWebApplicationFactory();
            var ex = await Assert.ThrowsAsync<PostgreSqlTestSafetyException>(() =>
                factory.EnsureDatabasePreflightAsync());

            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
            Assert.False(factory.IsPreflightVerified);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, original);
        }
    }

    // 16. Regression: PostgreSqlWebApplicationFactory client creation strictly requires opt-in
    [Fact]
    public void PostgreSqlWebApplicationFactory_CreateClient_WhenNotOptedIn_ThrowsSafetyException()
    {
        var original = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);

            using var factory = new PostgreSqlWebApplicationFactory();
            var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
                factory.CreateClient());

            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, original);
        }
    }
}
