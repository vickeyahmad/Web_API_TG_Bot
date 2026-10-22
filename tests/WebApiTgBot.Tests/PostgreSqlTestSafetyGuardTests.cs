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
        var remoteConnStr = "Host=remote.rds.amazonaws.com;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=SamplePassword123!";

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(remoteConnStr));

        Assert.Contains("Remote or unapproved database host", ex.Message);
        Assert.Contains("remote.rds.amazonaws.com", ex.Message);
    }

    [Fact]
    public void UnapprovedDatabasePort_IsStrictlyRejected()
    {
        var badPortConnStr = "Host=localhost;Port=9999;Database=webapitgbot_test;Username=gogaa_test_runner;Password=SamplePassword123!";

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(badPortConnStr));

        Assert.Contains("Database port 9999 is not the approved isolated test port", ex.Message);
    }

    // 6. Approved test database identity accepted in configuration validation
    [Theory]
    [InlineData("Host=localhost;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=SamplePassword123!")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=SamplePassword123!")]
    [InlineData("Host=::1;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=SamplePassword123!")]
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
        var invalidConnStr = $"Host=localhost;Port=5433;Database=webapitgbot_dev;Username=gogaa_test_runner;Password={secretPassword}";

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

            var validConnStr = "Host=localhost;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p";
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

    // ─────────────────────────────────────────────────────────────────────────────
    // MILESTONE 6B-6E3B2 PURE SAFETY TESTS
    // ─────────────────────────────────────────────────────────────────────────────

    // 17. Fail-closed preflight enforcement: ALL HTTP methods rejected before preflight
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PostgreSqlPreflightGuardedHandler_RejectsAllHttpMethods_WhenPreflightNotVerified(string method)
    {
        using var factory = new PostgreSqlWebApplicationFactory();
        Assert.False(factory.IsPreflightVerified);

        var innerHandler = new TestStubHttpMessageHandler();
        var guardedHandler = new PostgreSqlPreflightGuardedHandler(factory, innerHandler);
        var invoker = new HttpMessageInvoker(guardedHandler);

        var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost/api/admin/orders/ORD-1/items/1/manual-fulfillment/start");
        var ex = await Assert.ThrowsAsync<PostgreSqlTestSafetyException>(() =>
            invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains("before EnsureDatabasePreflightAsync() was completed", ex.Message);
        Assert.Contains(method, ex.Message);
    }

    // 18. Preflight guarded handler allows requests when verified
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PostgreSqlPreflightGuardedHandler_AllowsRequests_WhenPreflightIsVerified(string method)
    {
        using var factory = new PostgreSqlWebApplicationFactory();
        factory.SetPreflightVerifiedForSafetyTesting(true);
        Assert.True(factory.IsPreflightVerified);

        var innerHandler = new TestStubHttpMessageHandler();
        var guardedHandler = new PostgreSqlPreflightGuardedHandler(factory, innerHandler);
        var invoker = new HttpMessageInvoker(guardedHandler);

        var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost/api/admin/orders/ORD-1/items/1/manual-fulfillment/start");
        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    // 19. Regression: Mutating GET /api/customer/wallet and other GET requests are strictly blocked before preflight
    [Fact]
    public async Task PostgreSqlPreflightGuardedHandler_RejectsGetWallet_WhenPreflightNotVerified()
    {
        using var factory = new PostgreSqlWebApplicationFactory();
        Assert.False(factory.IsPreflightVerified);

        var innerHandler = new TestStubHttpMessageHandler();
        var guardedHandler = new PostgreSqlPreflightGuardedHandler(factory, innerHandler);
        var invoker = new HttpMessageInvoker(guardedHandler);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/customer/wallet");
        var ex = await Assert.ThrowsAsync<PostgreSqlTestSafetyException>(() =>
            invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains("before EnsureDatabasePreflightAsync() was completed", ex.Message);
        Assert.Contains("GET", ex.Message);
    }

    // 19b. Regression: PostgreSqlPreflightStartupFilter rejects GET and all methods inside the server pipeline
    [Theory]
    [InlineData("GET", "/api/customer/wallet")]
    [InlineData("POST", "/api/admin/orders/ORD-1/items/1/manual-fulfillment/start")]
    public async Task PostgreSqlPreflightStartupFilter_RejectsRequestsInPipeline_WhenPreflightNotVerified(string method, string path)
    {
        using var factory = new PostgreSqlWebApplicationFactory();
        Assert.False(factory.IsPreflightVerified);

        var filter = new PostgreSqlPreflightStartupFilter(factory);
        var builder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(null!);
        var configure = filter.Configure(_ => { });
        configure(builder);
        var requestDelegate = builder.Build();

        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        var ex = await Assert.ThrowsAsync<PostgreSqlTestSafetyException>(() =>
            requestDelegate(context));

        Assert.Contains("before EnsureDatabasePreflightAsync() was completed", ex.Message);
        Assert.Contains(method, ex.Message);
    }

    // 20. Failed preflight leaves verification state false
    [Fact]
    public void FailedPreflight_LeavesVerificationStateFalse()
    {
        using var factory = new PostgreSqlWebApplicationFactory();
        Assert.False(factory.IsPreflightVerified);

        factory.ResetPreflightStateForTesting();
        Assert.False(factory.IsPreflightVerified);

        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.AssertPreflightVerified(factory.IsPreflightVerified));
        Assert.Contains("Database preflight is not verified", ex.Message);
    }

    // 21. Fixture initialization requires verified preflight and opt-in
    [Fact]
    public void FixtureInitialization_RequiresVerifiedPreflight_ThrowsWhenUnmigrated()
    {
        var original = Environment.GetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, null);

            var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
                PostgreSqlTestDatabase.AssertMigrated());

            Assert.Contains("PostgreSQL integration test execution is disabled", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PostgreSqlTestSafetyGuard.OptInEnvironmentVariable, original);
        }
    }

    // 22. Target Session Attributes routing parameter rejected
    [Fact]
    public void TargetSessionAttributes_RoutingParameter_IsStrictlyRejected()
    {
        var connStr = "Host=localhost;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p;Target Session Attributes=primary";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(connStr));
        Assert.Contains("Target Session Attributes", ex.Message);
    }

    // 23. Service resolution parameter rejected
    [Fact]
    public void ServiceParameter_Resolution_IsStrictlyRejected()
    {
        var connStr = "Service=myservice;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(connStr));
        Assert.Contains("Service", ex.Message);
    }

    // 24. Passfile parameter rejected
    [Fact]
    public void PassfileParameter_IsStrictlyRejected()
    {
        var connStr = "Host=localhost;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Passfile=/path/to/.pgpass";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(connStr));
        Assert.Contains("Passfile", ex.Message);
    }

    // 25. Multi-host comma-separated hosts rejected
    [Theory]
    [InlineData("Host=localhost,remotehost;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p")]
    [InlineData("Host=localhost,127.0.0.1;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p")]
    public void MultiHostConnectionString_IsStrictlyRejected(string multiHostConnStr)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(multiHostConnStr));
        Assert.Contains("Multi-host or path-based database host", ex.Message);
    }

    // 26. Path-based / socket hosts rejected
    [Theory]
    [InlineData("Host=/var/run/postgresql;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p")]
    [InlineData(@"Host=\\.\pipe\postgresql;Port=5433;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p")]
    public void PathBasedHost_IsStrictlyRejected(string pathHostConnStr)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(pathHostConnStr));
        Assert.Contains("Multi-host or path-based database host", ex.Message);
    }

    // 26b. Development port 5432 is strictly rejected
    [Fact]
    public void DevelopmentPort5432_IsStrictlyRejected()
    {
        var devPortConnStr = "Host=localhost;Port=5432;Database=webapitgbot_test;Username=gogaa_test_runner;Password=p";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(devPortConnStr));

        Assert.Contains("Database port 5432 is the development PostgreSQL port", ex.Message);
        Assert.Contains("Tests must NEVER target the development database instance", ex.Message);
    }

    [Fact]
    public void ValidatePort_WhenDevelopmentPort5432_ThrowsDedicatedSafetyException()
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidatePort(5432));

        Assert.Contains("development PostgreSQL port", ex.Message);
    }

    // 26c. Database user validation: Prohibited development user webapitgbot is strictly rejected
    [Theory]
    [InlineData("webapitgbot")]
    [InlineData("postgres")]
    [InlineData("root")]
    [InlineData("admin")]
    [InlineData("administrator")]
    public void ProhibitedDatabaseUsers_AreStrictlyRejected(string prohibitedUser)
    {
        var connStr = $"Host=localhost;Port=5433;Database=webapitgbot_test;Username={prohibitedUser};Password=p";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(connStr));

        Assert.Contains("is strictly prohibited", ex.Message);
        Assert.Contains(prohibitedUser, ex.Message);
    }

    // 26d. Arbitrary unapproved database user is strictly rejected
    [Theory]
    [InlineData("testuser")]
    [InlineData("custom_user")]
    [InlineData("dev_runner")]
    [InlineData("qa_user")]
    public void ArbitraryDatabaseUser_IsStrictlyRejected(string arbitraryUser)
    {
        var connStr = $"Host=localhost;Port=5433;Database=webapitgbot_test;Username={arbitraryUser};Password=p";
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateConnectionString(connStr));

        Assert.Contains("is not the approved test runner role", ex.Message);
        Assert.Contains(PostgreSqlTestSafetyGuard.ApprovedDatabaseUser, ex.Message);
    }

    // 26e. Missing database username is strictly rejected
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingDatabaseUser_IsStrictlyRejected(string? emptyUser)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateDatabaseUser(emptyUser));

        Assert.Contains("Database username is missing", ex.Message);
    }

    // 26f. Approved test runner role is accepted
    [Fact]
    public void ApprovedDatabaseUser_IsAccepted()
    {
        // Validating the exact approved test runner role must succeed without throwing
        PostgreSqlTestSafetyGuard.ValidateDatabaseUser("gogaa_test_runner");
        PostgreSqlTestSafetyGuard.ValidateDatabaseUser("GOGAA_TEST_RUNNER");
    }

    // 26g. Actual reported database user from engine comparison: prohibited users rejected
    [Theory]
    [InlineData("webapitgbot")]
    [InlineData("postgres")]
    [InlineData("root")]
    [InlineData("admin")]
    public void ActualDatabaseUser_ProhibitedUser_IsStrictlyRejected(string actualUser)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateActualDatabaseUser(actualUser, PostgreSqlTestSafetyGuard.ApprovedDatabaseUser));

        Assert.Contains("strictly prohibited", ex.Message);
    }

    // 26h. Actual reported database user from engine comparison: mismatched user rejected
    [Theory]
    [InlineData("other_user")]
    [InlineData("testuser")]
    [InlineData("gogaa_dev_runner")]
    public void ActualDatabaseUser_MismatchedUser_IsStrictlyRejected(string actualUser)
    {
        var ex = Assert.Throws<PostgreSqlTestSafetyException>(() =>
            PostgreSqlTestSafetyGuard.ValidateActualDatabaseUser(actualUser, PostgreSqlTestSafetyGuard.ApprovedDatabaseUser));

        Assert.Contains("expected test role is 'gogaa_test_runner'", ex.Message);
    }

    // 26i. Actual reported database user from engine comparison: expected role matches
    [Fact]
    public void ActualDatabaseUser_ExpectedTestRunner_MatchesSuccessfully()
    {
        PostgreSqlTestSafetyGuard.ValidateActualDatabaseUser("gogaa_test_runner", PostgreSqlTestSafetyGuard.ApprovedDatabaseUser);
        PostgreSqlTestSafetyGuard.ValidateActualDatabaseUser("GOGAA_TEST_RUNNER", PostgreSqlTestSafetyGuard.ApprovedDatabaseUser);
    }

    private static bool HasPostgreSqlIntegrationTrait(System.Reflection.MemberInfo member)
    {
        return member.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute) &&
            a.ConstructorArguments.Count >= 2 &&
            string.Equals(a.ConstructorArguments[0].Value?.ToString(), "Category", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.ConstructorArguments[1].Value?.ToString(), "PostgreSqlIntegration", StringComparison.OrdinalIgnoreCase));
    }

    // 27. Test categorization discovery: ManualFulfillmentApiIntegrationTests has PostgreSqlIntegration trait
    [Fact]
    public void ManualFulfillmentApiIntegrationTests_HasPostgreSqlIntegrationTrait()
    {
        var type = typeof(ManualFulfillmentApiIntegrationTests);
        Assert.True(HasPostgreSqlIntegrationTrait(type), "ManualFulfillmentApiIntegrationTests must have Category=PostgreSqlIntegration at class level.");

        var testMethods = type.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), true).Any() ||
                        m.GetCustomAttributes(typeof(TheoryAttribute), true).Any())
            .ToList();

        Assert.Equal(30, testMethods.Count);
        foreach (var m in testMethods)
        {
            Assert.True(HasPostgreSqlIntegrationTrait(m), $"Method {m.Name} must have Category=PostgreSqlIntegration trait.");
        }
    }

    // 28. Test categorization discovery: WalletSystemTests has PostgreSqlIntegration trait
    [Fact]
    public void WalletSystemTests_HasPostgreSqlIntegrationTrait()
    {
        var type = typeof(WalletSystemTests);
        Assert.True(HasPostgreSqlIntegrationTrait(type), "WalletSystemTests must have Category=PostgreSqlIntegration at class level.");

        var testMethods = type.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), true).Any() ||
                        m.GetCustomAttributes(typeof(TheoryAttribute), true).Any())
            .ToList();

        Assert.Equal(18, testMethods.Count);
        foreach (var m in testMethods)
        {
            Assert.True(HasPostgreSqlIntegrationTrait(m), $"Method {m.Name} must have Category=PostgreSqlIntegration trait.");
        }
    }

    // 29. Pure safety tests are not mistakenly categorized as PostgreSQL-connected
    [Fact]
    public void PureSafetyTests_AreNotMistakenlyCategorizedAsPostgreSqlIntegration()
    {
        var type = typeof(PostgreSqlTestSafetyGuardTests);
        Assert.False(HasPostgreSqlIntegrationTrait(type), "PostgreSqlTestSafetyGuardTests must NOT have Category=PostgreSqlIntegration at class level.");

        var testMethods = type.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), true).Any() ||
                        m.GetCustomAttributes(typeof(TheoryAttribute), true).Any())
            .ToList();

        foreach (var m in testMethods)
        {
            Assert.False(HasPostgreSqlIntegrationTrait(m), $"Pure safety test {m.Name} must NOT have Category=PostgreSqlIntegration trait.");
        }
    }

    // 30. All PostgreSql_ methods across test classes are properly categorized
    [Fact]
    public void AllPostgreSqlMethods_AcrossTestClasses_AreProperlyCategorized()
    {
        var testAssembly = typeof(PostgreSqlTestSafetyGuardTests).Assembly;
        var testClasses = testAssembly.GetTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("Tests") && t != typeof(PostgreSqlTestSafetyGuardTests))
            .ToList();

        var pgMethods = testClasses
            .SelectMany(t => t.GetMethods())
            .Where(m => m.Name.StartsWith("PostgreSql_"))
            .ToList();

        Assert.True(pgMethods.Count >= 29, $"Expected at least 29 PostgreSql_ methods, but found {pgMethods.Count}.");

        foreach (var m in pgMethods)
        {
            var hasCategory = HasPostgreSqlIntegrationTrait(m) ||
                              (m.DeclaringType != null && HasPostgreSqlIntegrationTrait(m.DeclaringType));

            Assert.True(hasCategory, $"Method {m.DeclaringType?.Name}.{m.Name} must have Category=PostgreSqlIntegration trait.");
        }
    }

    private class TestStubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
