using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiTgBot.Infrastructure.Data;

namespace WebApiTgBot.Tests;

/// <summary>
/// A customized test application factory that registers the application
/// with an in-memory database instead of PostgreSQL.
///
/// Program.cs already skips the Npgsql registration in the "Test" environment,
/// so this factory simply adds AppDbContext using InMemory.
/// No provider conflict can occur because Npgsql is never registered in tests.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "TestDb_" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // This matches the "Test" environment check in Program.cs.
        // When this is set, Program.cs skips Npgsql and the migration block.
        builder.UseEnvironment("Test");

        builder.ConfigureServices(services =>
        {
            // Register AppDbContext using the InMemory provider.
            // All requests within this factory instance share the same test database.
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // Add a DbContext health check using the InMemory database.
            services.AddHealthChecks()
                .AddDbContextCheck<AppDbContext>("database");
        });
    }
}
