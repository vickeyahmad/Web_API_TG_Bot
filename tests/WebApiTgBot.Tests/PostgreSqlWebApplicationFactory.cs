using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Fulfillments.Options;
using WebApiTgBot.Modules.Payments.Options;

namespace WebApiTgBot.Tests;

/// <summary>
/// A focused test-only application factory that registers the application
/// with an approved, isolated PostgreSQL test database connection.
///
/// Safety Controls:
/// 1. Enforces explicit test opt-in (GOGAA_ENABLE_POSTGRES_TESTS=true) on every configuration path.
/// 2. Retrieves connection string exclusively via PostgreSqlTestSafetyGuard, enforcing explicit opt-in
///    and validating target database identity ("webapitgbot_test") on approved local host/port.
/// 3. No custom connection string parameter allowed in constructors, completely eliminating authorization bypasses.
/// 4. Uses builder.UseEnvironment("Test"), ensuring Program.cs skips the application DefaultConnection
///    and skips automatic startup migrations.
/// 5. Does NOT run database migrations or open connections during factory construction.
/// 6. Requires explicit EnsureDatabasePreflightAsync() to verify engine-level identity (SELECT current_database())
///    before database mutations occur.
/// 7. Preserves all real ASP.NET Core routing, cookie authentication, CSRF token validation,
///    controller action filters, and business service behavior.
/// 8. Provides test-only helper to override ManualFulfillmentOptions strictly in-memory.
/// </summary>
public class PostgreSqlWebApplicationFactory : WebApplicationFactory<Program>
{
    public PostgreSqlWebApplicationFactory()
    {
    }

    /// <summary>
    /// Indicates whether engine-level database identity preflight and migrations have been verified.
    /// </summary>
    public bool IsPreflightVerified { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Enforce explicit opt-in authorization immediately before configuring host
        PostgreSqlTestSafetyGuard.AssertExecutionAllowed();

        // Matches the "Test" environment check in Program.cs.
        // Prevents Program.cs from registering DefaultConnection or running startup migrations.
        builder.UseEnvironment("Test");

        builder.ConfigureServices(services =>
        {
            // Retrieve approved test connection string through the safety guard
            // Guarantees opt-in, non-empty env var, and strict identity validation
            var connectionString = PostgreSqlTestSafetyGuard.GetApprovedConnectionString();

            // Register AppDbContext with approved PostgreSQL connection
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Register database health check with PostgreSQL
            services.AddHealthChecks()
                .AddDbContextCheck<AppDbContext>("database");
        });
    }

    /// <summary>
    /// Executes pre-flight engine-level validation and ensures migrations are applied safely.
    /// Verifies:
    /// 1. Opt-in authorization (GOGAA_ENABLE_POSTGRES_TESTS=true).
    /// 2. Connection string safety (webapitgbot_test on localhost:5432).
    /// 3. Connectivity check.
    /// 4. Live engine validation via 'SELECT current_database(), current_user;'.
    /// 5. Safe idempotent migration via PostgreSqlTestDatabase.
    /// Must be invoked by integration test fixtures before issuing mutative HTTP requests.
    /// </summary>
    public async Task EnsureDatabasePreflightAsync()
    {
        PostgreSqlTestSafetyGuard.AssertExecutionAllowed();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PostgreSqlTestDatabase.EnsureMigratedAsync(db);
        IsPreflightVerified = true;
    }

    /// <summary>
    /// Creates an HttpClient configured with test-only feature flags and approved SKUs.
    /// Overrides options strictly in memory for this client, preserving normal defaults.
    /// </summary>
    public HttpClient CreateEnabledClient(List<string>? approvedSkus = null)
    {
        return WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<ManualFulfillmentOptions>(options =>
                {
                    options.Enabled = true;
                    if (approvedSkus != null)
                    {
                        options.ApprovedProductSkus = approvedSkus;
                    }
                });
                services.Configure<WalletPurchaseOptions>(options =>
                {
                    options.Enabled = true;
                    options.Currency = "PKR";
                });
                services.Configure<WalletTopUpOptions>(options =>
                {
                    options.Enabled = true;
                });
            });
        }).CreateClient();
    }
}
