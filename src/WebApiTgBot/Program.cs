using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Services;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Services;
using WebApiTgBot.Modules.Orders.Services;

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
    builder.Services.AddHealthChecks();
}

// ── ASP.NET Core Identity ─────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
{
    // Password policy: strong default requirements
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;

    // Account lockout policy: mitigate brute-force password guessing
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    // User policy
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// ── Security Stamp Validation Policy ──────────────────────────────────────────
// Configure validation interval to zero for admin sessions so that security-sensitive
// changes (password resets, security stamp updates, role revocations) take effect immediately.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
});

// ── Authentication Cookie Configuration ───────────────────────────────────────
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".WebAPI.Admin.Auth";
    options.Cookie.HttpOnly = true;
    // SameSite=Lax allows same-site cross-subdomain fetch requests (admin.gogaabyte.com -> api.gogaabyte.com)
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // In a Web API, return 401 Unauthorized / 403 Forbidden instead of HTTP 302 redirects
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };

    // CRITICAL SECURITY ENFORCEMENT:
    // Preserves built-in Identity security stamp validation AND enforces immediate active/role checks.
    options.Events.OnValidatePrincipal = async context =>
    {
        // 1. Invoke built-in Identity security stamp validation first
        await SecurityStampValidator.ValidatePrincipalAsync(context);

        // If built-in validation invalidated the principal, stop immediately
        if (context.Principal == null || context.Principal.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // 2. Perform immediate active status and role verification on every request
        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(context.Principal);
        if (user == null || !user.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
            return;
        }

        var isAdmin = await userManager.IsInRoleAsync(user, AppRoles.Admin);
        if (!isAdmin)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
            return;
        }
    };
});

// Dedicated Customer Authentication Cookie Scheme (.WebAPI.Customer.Auth)
// Strictly isolated from administrator sessions at the cookie and handler level.
builder.Services.AddAuthentication()
    .AddCookie(AuthSchemes.CustomerScheme, options =>
    {
        options.Cookie.Name = ".WebAPI.Customer.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        // In a Web API, return 401 Unauthorized / 403 Forbidden instead of HTTP 302 redirects
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Preserves built-in Identity security stamp validation AND enforces immediate active/customer checks
        options.Events.OnValidatePrincipal = async context =>
        {
            // 1. Invoke built-in Identity security stamp validation first
            await SecurityStampValidator.ValidatePrincipalAsync(context);

            if (context.Principal == null || context.Principal.Identity?.IsAuthenticated != true)
            {
                return;
            }

            // 2. Perform immediate active status and Customer role verification on every request
            var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.GetUserAsync(context.Principal);
            if (user == null || !user.IsActive)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.CustomerScheme);
                return;
            }

            var isCustomer = await userManager.IsInRoleAsync(user, AppRoles.Customer);
            if (!isCustomer)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(AuthSchemes.CustomerScheme);
                return;
            }
        };
    });

// ── Rate Limiting ─────────────────────────────────────────────────────────────
var permitLimit = builder.Environment.IsEnvironment("Test") ? 1000 : 10;
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("AdminAuthLimiter", opt =>
    {
        opt.PermitLimit = permitLimit;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("CustomerAuthLimiter", opt =>
    {
        opt.PermitLimit = permitLimit;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("CustomerOrderLimiter", opt =>
    {
        opt.PermitLimit = permitLimit;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Antiforgery / CSRF ────────────────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = ".WebAPI.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsProduction()
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});

// ── CORS Policy ───────────────────────────────────────────────────────────────
// Restrictive policy for future frontends (admin.gogaabyte.com, tools.gogaabyte.com, smm.gogaabyte.com).
// Arbitrary origins or wildcards with credentials are strictly forbidden.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? (builder.Environment.IsDevelopment()
        ? new[] { "http://localhost:3000", "https://localhost:3000", "http://localhost:3001", "https://localhost:3001" }
        : new[] { "https://admin.gogaabyte.com", "https://tools.gogaabyte.com", "https://smm.gogaabyte.com", "https://api.gogaabyte.com" });

builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminCorsPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .WithHeaders("Content-Type", "X-XSRF-TOKEN", "Authorization", "Accept")
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .AllowCredentials();
    });
});

// ── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IAdminCatalogService, AdminCatalogService>();
builder.Services.AddScoped<IAdminVendorService, AdminVendorService>();
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<ICustomerAuthService, CustomerAuthService>();
builder.Services.AddScoped<ICustomerOrderService, CustomerOrderService>();
builder.Services.AddScoped<IAdminSeeder, AdminSeeder>();
builder.Services.AddSingleton<IOrderNumberGenerator, OrderNumberGenerator>();




// ── Controllers & Centralized CSRF Protection ────────────────────────────────
// Centralized defense-in-depth:
// Automatically validates Antiforgery / CSRF tokens on ALL mutative HTTP methods (POST, PUT, PATCH, DELETE)
// across all existing and future controllers, ensuring future admin controllers cannot accidentally bypass CSRF.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

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

// ── Swagger UI (development only) ────────────────────────────────────
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
app.UseCors("AdminCorsPolicy");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Apply database migrations and seed administrative accounts on startup ─────
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

    var seeder = scope.ServiceProvider.GetRequiredService<IAdminSeeder>();
    await seeder.SeedAsync();
}

app.Run();

// Required for integration test WebApplicationFactory to access Program
public partial class Program { }
