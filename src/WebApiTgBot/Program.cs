using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
// In Test environment: database is configured by the test factory (InMemory).
// In all other environments: connect to PostgreSQL using the connection string.
// The connection string MUST come from environment variables or user-secrets.
// NEVER put a real password in appsettings.json or source code.
if (!builder.Environment.IsEnvironment("Test"))
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Database connection string 'DefaultConnection' is not configured. " +
            "Set the ConnectionStrings__DefaultConnection environment variable or use dotnet user-secrets.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    // Adds a database connectivity check that pings PostgreSQL.
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database");
}
else
{
    // In Test environment: the test factory registers AppDbContext with InMemory.
    // We register a simple always-healthy check here.
    builder.Services.AddHealthChecks();
}

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<ICatalogService, CatalogService>();

// ── Controllers & API ─────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Swagger / OpenAPI (development only) ─────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Web API TG Bot",
        Version = "v1",
        Description = "Centralized B2C Digital Reseller Platform API"
    });
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();
// ─────────────────────────────────────────────────────────────────────────────

// ── Swagger UI (development only) ────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Web API TG Bot v1");
        options.RoutePrefix = "swagger";
    });
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// ── Apply pending database migrations on startup ──────────────────────────────
// Only runs for real relational databases (PostgreSQL) in non-Test environments.
// Skipped automatically in the Test environment (used by automated tests).
if (!app.Environment.IsEnvironment("Test"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        db.Database.Migrate();
    }
}

app.Run();

// Required for integration test WebApplicationFactory to access Program
public partial class Program { }
