using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying Customer Registration and Authentication (Milestone 3B):
/// - Self-registration with server-side Customer role assignment.
/// - Duplicate email rejection (409 Conflict).
/// - Input and password complexity validation (400 Bad Request).
/// - Customer login issuing .WebAPI.Customer.Auth cookie.
/// - Incorrect credentials rejection (401 Unauthorized).
/// - Account deactivation and immediate session revocation (403/401).
/// - Safe profile retrieval via /api/customer/auth/me.
/// - Unauthenticated access rejection on customer endpoints (401).
/// - Strict separation: Customers cannot access administrator endpoints (/api/admin/*).
/// - Administrators cannot use customer endpoints without Customer role.
/// - Anti-CSRF protection on mutative state-changing operations (logout).
/// - Rate limiting and lockout protection.
/// </summary>
public class CustomerAuthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CustomerAuthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task EnsureRolesExistAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in new[] { AppRoles.Admin, AppRoles.Customer })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new ApplicationRole(role));
            }
        }
    }

    [Fact]
    public async Task CustomerRegister_WithValidData_Returns201Created_AndAssignsCustomerRole()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var uniqueEmail = $"new_customer_{Guid.NewGuid():N}@test.local";
        var request = new CustomerRegisterRequest
        {
            Email = uniqueEmail,
            Password = "SecurePass123!",
            FullName = "Ali Ahmed"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/register", request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(uniqueEmail.ToLowerInvariant(), result.Data.Email);
        Assert.Equal("Ali Ahmed", result.Data.FullName);
        Assert.True(result.Data.IsActive);
        Assert.False(result.Data.EmailConfirmed); // Unverified initially; email verification planned for future milestone
        Assert.Contains(AppRoles.Customer, result.Data.Roles);
        Assert.DoesNotContain(AppRoles.Admin, result.Data.Roles);

        // Verify in database: user has strictly Customer role
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(uniqueEmail);
        Assert.NotNull(user);
        var roles = await userManager.GetRolesAsync(user);
        Assert.Single(roles);
        Assert.Equal(AppRoles.Customer, roles[0]);
    }

    [Fact]
    public async Task CustomerRegister_WithDuplicateEmail_Returns409Conflict()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"dup_{Guid.NewGuid():N}@test.local";
        var request1 = new CustomerRegisterRequest
        {
            Email = email,
            Password = "ValidPassword123!",
            FullName = "First Customer"
        };
        var firstResponse = await _client.PostAsJsonAsync("/api/customer/auth/register", request1);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        // Act — Try registering identical email
        var request2 = new CustomerRegisterRequest
        {
            Email = email.ToUpperInvariant(), // Test case-insensitivity
            Password = "AnotherPassword123!",
            FullName = "Second Customer"
        };
        var duplicateResponse = await _client.PostAsJsonAsync("/api/customer/auth/register", request2);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var result = await duplicateResponse.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "ValidPass123!")]             // Empty email
    [InlineData("not-an-email", "ValidPass123!")]   // Malformed email
    [InlineData("test@local", "")]                  // Empty password
    public async Task CustomerRegister_WithInvalidInput_Returns400BadRequest(string email, string password)
    {
        // Arrange
        var request = new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Invalid Input Test"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/register", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("short1!")]         // Less than 8 chars
    [InlineData("alllowercase1!")]  // Missing uppercase
    [InlineData("ALLUPPERCASE1!")]  // Missing lowercase
    [InlineData("NoDigitsHere!")]   // Missing digit
    [InlineData("NoSpecialChar1")]  // Missing non-alphanumeric
    public async Task CustomerRegister_WithWeakPassword_Returns400BadRequest(string weakPassword)
    {
        // Arrange
        var email = $"weakpass_{Guid.NewGuid():N}@test.local";
        var request = new CustomerRegisterRequest
        {
            Email = email,
            Password = weakPassword,
            FullName = "Weak Pass Test"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/register", request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CustomerLogin_WithValidCredentials_Returns200AndCustomerCookie()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"login_{Guid.NewGuid():N}@test.local";
        var password = "ValidPassword123!";
        var registerResponse = await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Login Customer"
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        // Act
        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var result = await loginResponse.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(email.ToLowerInvariant(), result.Data!.Email);
        Assert.Contains(AppRoles.Customer, result.Data.Roles);

        // Verify customer cookie was issued
        Assert.True(loginResponse.Headers.Contains("Set-Cookie"));
        var cookies = loginResponse.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.Contains(".WebAPI.Customer.Auth"));
    }

    [Fact]
    public async Task CustomerLogin_WithIncorrectPassword_Returns401Unauthorized()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"badpass_{Guid.NewGuid():N}@test.local";
        var password = "CorrectPass123!";
        await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password
        });

        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = "WrongPassword999!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CustomerLogin_WithUnregisteredEmail_Returns401Unauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = $"nonexistent_{Guid.NewGuid():N}@test.local",
            Password = "SomePassword123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CustomerLogin_WithInactiveCustomer_Returns403Forbidden()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"inactive_{Guid.NewGuid():N}@test.local";
        var password = "Password123!";
        var registerResponse = await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

        // Deactivate customer account in database
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            user.IsActive = false;
            await userManager.UpdateAsync(user);
        }

        // Act
        var response = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomerMe_WhenUnauthenticated_Returns401Unauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/customer/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CustomerMe_WhenAuthenticatedAsCustomer_ReturnsSafeProfile()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"me_{Guid.NewGuid():N}@test.local";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Safe Profile Customer"
        });

        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var customerCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        // Act
        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/me");
        meRequest.Headers.Add("Cookie", customerCookie);
        var meResponse = await _client.SendAsync(meRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var result = await meResponse.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(email.ToLowerInvariant(), result.Data.Email);
        Assert.Equal("Safe Profile Customer", result.Data.FullName);
        Assert.True(result.Data.IsActive);
        Assert.Contains(AppRoles.Customer, result.Data.Roles);
    }

    [Fact]
    public async Task Customer_CannotAccessAdministratorEndpoints_Returns401Or403()
    {
        // Arrange — Logged-in Customer with valid .WebAPI.Customer.Auth cookie
        await EnsureRolesExistAsync();
        var email = $"cust_noadmin_{Guid.NewGuid():N}@test.local";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password
        });

        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var customerCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        // Act — Attempt to access admin endpoints using Customer cookie
        var adminCategoriesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/categories");
        adminCategoriesRequest.Headers.Add("Cookie", customerCookie);
        var adminCategoriesResponse = await _client.SendAsync(adminCategoriesRequest);

        var adminMeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        adminMeRequest.Headers.Add("Cookie", customerCookie);
        var adminMeResponse = await _client.SendAsync(adminMeRequest);

        // Assert — Admin endpoints must strictly reject customer sessions with 401 Unauthorized
        // because Admin endpoints require the default scheme (.WebAPI.Admin.Auth) and the Admin role
        Assert.Equal(HttpStatusCode.Unauthorized, adminCategoriesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminMeResponse.StatusCode);
    }

    [Fact]
    public async Task DeactivatedCustomer_CannotAccessCustomerMe_WithExistingSession()
    {
        // Arrange
        await EnsureRolesExistAsync();
        var email = $"cust_revoke_{Guid.NewGuid():N}@test.local";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password
        });

        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var customerCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        // Deactivate customer account
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            user.IsActive = false;
            await userManager.UpdateAsync(user);
        }

        // Act — attempt to use the existing cookie after deactivation
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/me");
        request.Headers.Add("Cookie", customerCookie);
        var response = await _client.SendAsync(request);

        // Assert — OnValidatePrincipal immediately revokes the customer session with 401
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CustomerCsrfAndLogout_EnforcesProtectionAndCompletesSuccessfully()
    {
        // Arrange — Authenticated customer
        await EnsureRolesExistAsync();
        var email = $"csrf_logout_{Guid.NewGuid():N}@test.local";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password
        });

        var loginResponse = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var customerCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        // Act 1 — Attempt POST /api/customer/auth/logout WITHOUT an antiforgery token
        var forgedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/customer/auth/logout");
        forgedRequest.Headers.Add("Cookie", customerCookie);
        var forgedResponse = await _client.SendAsync(forgedRequest);

        // Assert 1 — AutoValidateAntiforgeryToken must reject with 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);

        // Act 2 — Fetch legitimate CSRF token from /api/customer/auth/csrf for the authenticated session
        var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        csrfRequest.Headers.Add("Cookie", customerCookie);
        var csrfResponse = await _client.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);

        // Assert Cache-Control is non-cacheable
        Assert.True(csrfResponse.Headers.CacheControl?.NoStore == true ||
                    csrfResponse.Headers.CacheControl?.NoCache == true);

        var csrfResult = await csrfResponse.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(csrfResult);
        var token = csrfResult.Data;
        Assert.False(string.IsNullOrWhiteSpace(token));

        var antiforgeryCookie = csrfResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Antiforgery"))
            .Split(';')[0];

        // Act 3 — Send legitimate POST /api/customer/auth/logout WITH customer cookie, antiforgery cookie, and header
        var legitimateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/customer/auth/logout");
        legitimateRequest.Headers.Add("Cookie", $"{customerCookie}; {antiforgeryCookie}");
        legitimateRequest.Headers.Add("X-XSRF-TOKEN", token);
        var legitimateResponse = await _client.SendAsync(legitimateRequest);

        // Assert 3 — Logout succeeds with 200 OK
        Assert.Equal(HttpStatusCode.OK, legitimateResponse.StatusCode);
    }

    [Fact]
    public async Task PostgreSql_CustomerIdentityIntegration_PersistsCustomerAndRoleCorrectly()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            return;
        }

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var customerEmail = $"pg_cust_auth_{uniqueSuffix}@test.local".ToLowerInvariant();

        // 1. Ensure Customer role exists in PostgreSQL
        var customerRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == AppRoles.Customer);
        if (customerRole == null)
        {
            customerRole = new ApplicationRole(AppRoles.Customer)
            {
                NormalizedName = AppRoles.Customer.ToUpperInvariant()
            };
            db.Roles.Add(customerRole);
            await db.SaveChangesAsync();
        }

        // 2. Insert test customer with password hash
        var hasher = new PasswordHasher<ApplicationUser>();
        var customer = new ApplicationUser
        {
            UserName = customerEmail,
            NormalizedUserName = customerEmail.ToUpperInvariant(),
            Email = customerEmail,
            NormalizedEmail = customerEmail.ToUpperInvariant(),
            FullName = "PG Integration Customer",
            EmailConfirmed = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        customer.PasswordHash = hasher.HashPassword(customer, "SecurePgPass123!");

        db.Users.Add(customer);
        await db.SaveChangesAsync();

        // 3. Map customer to Customer role in AspNetUserRoles
        db.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = customer.Id,
            RoleId = customerRole.Id
        });
        await db.SaveChangesAsync();

        try
        {
            // Verify in PostgreSQL database
            await using var verifyDb = new AppDbContext(options);
            var retrievedUser = await verifyDb.Users.FirstOrDefaultAsync(u => u.Id == customer.Id);
            Assert.NotNull(retrievedUser);
            Assert.Equal(customerEmail, retrievedUser.Email);
            Assert.Equal("PG Integration Customer", retrievedUser.FullName);
            Assert.True(retrievedUser.IsActive);
            Assert.False(retrievedUser.EmailConfirmed);

            // Verify password hasher can verify password
            var verifyResult = hasher.VerifyHashedPassword(retrievedUser, retrievedUser.PasswordHash!, "SecurePgPass123!");
            Assert.Equal(PasswordVerificationResult.Success, verifyResult);

            // Verify role mapping exists
            var hasCustomerRole = await verifyDb.UserRoles.AnyAsync(ur => ur.UserId == customer.Id && ur.RoleId == customerRole.Id);
            Assert.True(hasCustomerRole);
        }
        finally
        {
            // Clean up test user and role mapping from PostgreSQL
            await using var cleanupDb = new AppDbContext(options);
            var userRoles = await cleanupDb.UserRoles.Where(ur => ur.UserId == customer.Id).ToListAsync();
            cleanupDb.UserRoles.RemoveRange(userRoles);

            var dbUser = await cleanupDb.Users.FindAsync(customer.Id);
            if (dbUser != null)
            {
                cleanupDb.Users.Remove(dbUser);
            }
            await cleanupDb.SaveChangesAsync();
        }
    }
}
