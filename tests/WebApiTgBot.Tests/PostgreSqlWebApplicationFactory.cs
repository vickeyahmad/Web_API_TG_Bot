using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// 7. Mandatory fail-closed preflight enforcement: any HTTP request (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS)
///    dispatched before EnsureDatabasePreflightAsync() completes throws PostgreSqlTestSafetyException.
/// 8. Thread-safe preflight synchronization via SemaphoreSlim ensures concurrent requests cannot race around preflight.
/// 9. Failed preflight leaves IsPreflightVerified false.
/// 10. Preserves all real ASP.NET Core routing, cookie authentication, CSRF token validation,
///    controller action filters, and business service behavior.
/// 11. Provides test-only helper to override ManualFulfillmentOptions strictly in-memory.
/// </summary>
public class PostgreSqlWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SemaphoreSlim _preflightLock = new(1, 1);
    private volatile bool _isPreflightVerified = false;

    public PostgreSqlWebApplicationFactory()
    {
    }

    /// <summary>
    /// Indicates whether engine-level database identity preflight and migrations have been verified.
    /// Thread-safe and fail-closed.
    /// </summary>
    public bool IsPreflightVerified => _isPreflightVerified;

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

            // Defense-in-depth: Test-only preflight startup filter enforces fail-closed protection
            // inside the ASP.NET Core request pipeline for any HTTP request before preflight.
            services.AddSingleton<IStartupFilter>(new PostgreSqlPreflightStartupFilter(this));
        });
    }

    /// <summary>
    /// Creates an HttpClient configured with mandatory preflight enforcement.
    /// Intercepts all HTTP requests and throws PostgreSqlTestSafetyException if preflight was omitted.
    /// </summary>
    public new HttpClient CreateClient()
    {
        return CreateDefaultClient(new PostgreSqlPreflightGuardedHandler(this));
    }

    /// <summary>
    /// Creates an HttpClient configured with the specified client options and mandatory preflight enforcement.
    /// </summary>
    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        var client = CreateDefaultClient(options.BaseAddress, new PostgreSqlPreflightGuardedHandler(this));
        ConfigureClient(client);
        return client;
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
    /// Thread-safe: concurrent calls serialize without race conditions.
    /// </summary>
    public async Task EnsureDatabasePreflightAsync()
    {
        PostgreSqlTestSafetyGuard.AssertExecutionAllowed();

        if (_isPreflightVerified)
        {
            return;
        }

        await _preflightLock.WaitAsync();
        try
        {
            if (_isPreflightVerified)
            {
                return;
            }

            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PostgreSqlTestDatabase.EnsureMigratedAsync(db);

            // Set verified ONLY after database engine verification and migrations finish successfully
            _isPreflightVerified = true;
        }
        catch
        {
            _isPreflightVerified = false;
            throw;
        }
        finally
        {
            _preflightLock.Release();
        }
    }

    /// <summary>
    /// Creates an HttpClient configured with test-only feature flags and approved SKUs.
    /// Overrides options strictly in memory for this client, preserving normal defaults.
    /// Enforces mandatory preflight via PostgreSqlPreflightGuardedHandler.
    /// </summary>
    public HttpClient CreateEnabledClient(List<string>? approvedSkus = null)
    {
        var derivedFactory = WithWebHostBuilder(builder =>
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
        });

        return derivedFactory.CreateDefaultClient(new PostgreSqlPreflightGuardedHandler(this));
    }

    /// <summary>
    /// Resets the preflight verification state. Used strictly for pure safety testing.
    /// </summary>
    public void ResetPreflightStateForTesting()
    {
        _isPreflightVerified = false;
    }

    /// <summary>
    /// Marks preflight verified without network access. Used strictly for pure unit testing
    /// of client dispatch guards.
    /// </summary>
    internal void SetPreflightVerifiedForSafetyTesting(bool verified)
    {
        _isPreflightVerified = verified;
    }
}

/// <summary>
/// Client-side HTTP delegating handler that intercepts all test HTTP requests (GET, POST, PUT, PATCH, DELETE, etc.)
/// and fails closed if mandatory database preflight was omitted or failed.
/// </summary>
public class PostgreSqlPreflightGuardedHandler : DelegatingHandler
{
    private readonly PostgreSqlWebApplicationFactory _factory;

    public PostgreSqlPreflightGuardedHandler(PostgreSqlWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public PostgreSqlPreflightGuardedHandler(PostgreSqlWebApplicationFactory factory, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _factory = factory;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_factory.IsPreflightVerified)
        {
            throw new PostgreSqlTestSafetyException(
                $"SAFETY VIOLATION: HTTP request '{request.Method} {request.RequestUri?.PathAndQuery}' was attempted " +
                $"against PostgreSqlWebApplicationFactory before EnsureDatabasePreflightAsync() was completed. " +
                $"All PostgreSQL-backed HTTP tests must verify database preflight before dispatching any HTTP requests.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Test-only ASP.NET Core startup filter registering pipeline middleware to reject any HTTP requests
/// before database preflight verification is complete.
/// </summary>
public class PostgreSqlPreflightStartupFilter : IStartupFilter
{
    private readonly PostgreSqlWebApplicationFactory _factory;

    public PostgreSqlPreflightStartupFilter(PostgreSqlWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!_factory.IsPreflightVerified)
                {
                    throw new PostgreSqlTestSafetyException(
                        $"SAFETY VIOLATION: Pipeline request '{context.Request.Method} {context.Request.Path}' was attempted " +
                        $"before EnsureDatabasePreflightAsync() was completed. " +
                        $"All PostgreSQL-backed HTTP tests must verify database preflight before dispatching any HTTP requests.");
                }

                await nextMiddleware();
            });

            next(app);
        };
    }
}
