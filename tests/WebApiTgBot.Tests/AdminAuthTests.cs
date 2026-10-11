using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Identity.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Integration tests verifying administrator authentication, authorization,
/// session cookies, logout, lockout protection, role boundaries, CSRF/Antiforgery,
/// security stamp validation, and immediate session revocation.
/// </summary>
public class AdminAuthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminAuthTests(CustomWebApplicationFactory factory)
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

    private async Task<ApplicationUser> CreateTestUserAsync(string email, string password, string role, bool isActive = true)
    {
        await EnsureRolesExistAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var existing = await userManager.FindByEmailAsync(email);
        if (existing != null)
        {
            return existing;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = "Test " + role,
            EmailConfirmed = true,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        if (!string.IsNullOrEmpty(role))
        {
            await userManager.AddToRoleAsync(user, role);
        }

        return user;
    }

    [Fact]
    public async Task AdminLogin_WithValidCredentials_ReturnsOkAndAuthCookie()
    {
        // Arrange
        var email = $"admin_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var loginRequest = new AdminLoginRequest
        {
            Email = email,
            Password = password,
            RememberMe = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", loginRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(email, result.Data!.Email);
        Assert.Contains(AppRoles.Admin, result.Data!.Roles);

        // Verify Set-Cookie header contains the auth cookie
        Assert.True(response.Headers.Contains("Set-Cookie"));
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.Contains(".WebAPI.Admin.Auth"));
    }

    [Fact]
    public async Task AdminLogin_WithIncorrectPassword_ReturnsUnauthorized()
    {
        // Arrange
        var email = $"admin_{Guid.NewGuid():N}@test.local";
        await CreateTestUserAsync(email, "CorrectPass123!", AppRoles.Admin);

        var loginRequest = new AdminLoginRequest
        {
            Email = email,
            Password = "WrongPassword999!"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", loginRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task AdminLogin_WithNonAdminUser_ReturnsForbidden()
    {
        // Arrange — customer account trying to log into the admin portal
        var email = $"customer_{Guid.NewGuid():N}@test.local";
        var password = "CustomerPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Customer);

        var loginRequest = new AdminLoginRequest
        {
            Email = email,
            Password = password
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", loginRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("Administrator privileges required", result.Message);
    }

    [Fact]
    public async Task AdminLogin_WithInactiveAdmin_ReturnsForbidden()
    {
        // Arrange — deactivated admin account
        var email = $"inactive_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin, isActive: false);

        var loginRequest = new AdminLoginRequest
        {
            Email = email,
            Password = password
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", loginRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("deactivated", result.Message);
    }

    [Fact]
    public async Task AdminMe_WhenUnauthenticated_ReturnsUnauthorized()
    {
        // Act — call /api/admin/auth/me without an authentication cookie
        var response = await _client.GetAsync("/api/admin/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminMe_WhenAuthenticatedAsAdmin_ReturnsCurrentProfile()
    {
        // Arrange
        var email = $"admin_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Extract authentication cookie
        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Act — make authenticated request
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        request.Headers.Add("Cookie", authCookie);
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(email, result.Data!.Email);
        Assert.Contains(AppRoles.Admin, result.Data!.Roles);
    }

    [Fact]
    public async Task DeactivatedAdmin_CannotAccessAdminEndpoints_WithExistingSession()
    {
        // Arrange — Active admin logs in and acquires a valid session cookie
        var email = $"revoked_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        var user = await CreateTestUserAsync(email, password, AppRoles.Admin, isActive: true);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Verify session works initially
        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        initialRequest.Headers.Add("Cookie", authCookie);
        var initialResponse = await _client.SendAsync(initialRequest);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

        // Deactivate the administrator in the database
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            dbUser.IsActive = false;
            await userManager.UpdateAsync(dbUser);
        }

        // Act — attempt to use the existing session cookie after deactivation
        var postRevocationRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        postRevocationRequest.Headers.Add("Cookie", authCookie);
        var postRevocationResponse = await _client.SendAsync(postRevocationRequest);

        // Assert — OnValidatePrincipal immediately revokes the session and returns 401
        Assert.Equal(HttpStatusCode.Unauthorized, postRevocationResponse.StatusCode);
    }

    [Fact]
    public async Task DemotedAdmin_CannotAccessAdminEndpoints_WithExistingSession()
    {
        // Arrange — Active admin logs in and acquires a valid session cookie
        var email = $"demoted_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        var user = await CreateTestUserAsync(email, password, AppRoles.Admin, isActive: true);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Demote the administrator (remove from Admin role)
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            await userManager.RemoveFromRoleAsync(dbUser, AppRoles.Admin);
        }

        // Act — attempt to access admin endpoint with the previously valid cookie
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        request.Headers.Add("Cookie", authCookie);
        var response = await _client.SendAsync(request);

        // Assert — OnValidatePrincipal verifies role and immediately rejects the session with 401
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SecurityStampUpdate_InvalidatesExistingSessionCookie()
    {
        // Arrange — Active admin logs in and acquires a valid session cookie
        var email = $"secstamp_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        var user = await CreateTestUserAsync(email, password, AppRoles.Admin, isActive: true);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Verify session works initially
        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        initialRequest.Headers.Add("Cookie", authCookie);
        var initialResponse = await _client.SendAsync(initialRequest);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

        // Trigger security-sensitive event: update security stamp (as occurs during password reset or remote signout)
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var dbUser = await userManager.FindByIdAsync(user.Id);
            Assert.NotNull(dbUser);
            await userManager.UpdateSecurityStampAsync(dbUser);
        }

        // Act — attempt to use the old session cookie after security stamp update
        var postRevocationRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        postRevocationRequest.Headers.Add("Cookie", authCookie);
        var postRevocationResponse = await _client.SendAsync(postRevocationRequest);

        // Assert — SecurityStampValidator immediately invalidates the principal, returning 401
        Assert.Equal(HttpStatusCode.Unauthorized, postRevocationResponse.StatusCode);
    }

    [Fact]
    public async Task CsrfProtection_CrossSubdomainReadiness_ReturnsNonCacheableJsonToken()
    {
        // Arrange — Authenticated admin
        var email = $"csrf_cross_{Guid.NewGuid():N}@test.local";
        var password = "AdminPass123!";
        await CreateTestUserAsync(email, password, AppRoles.Admin);

        var loginResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        // Act 1 — Attempt POST /api/admin/auth/logout WITHOUT an antiforgery token
        var forgedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/logout");
        forgedRequest.Headers.Add("Cookie", authCookie);
        var forgedResponse = await _client.SendAsync(forgedRequest);

        // Assert 1 — AutoValidateAntiforgeryToken must reject the request with 400 Bad Request
        Assert.Equal(HttpStatusCode.BadRequest, forgedResponse.StatusCode);

        // Act 2 — Fetch legitimate CSRF token from the API
        var csrfResponse = await _client.GetAsync("/api/admin/auth/csrf");
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

        // Assert Antiforgery cookie is HttpOnly for security
        var fullAntiforgerySetCookie = csrfResponse.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Antiforgery"));
        Assert.Contains("httponly", fullAntiforgerySetCookie, StringComparison.OrdinalIgnoreCase);

        // Act 3 — Send legitimate POST /api/admin/auth/logout WITH both cookies and X-XSRF-TOKEN header from JSON body
        var legitimateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/auth/logout");
        legitimateRequest.Headers.Add("Cookie", $"{authCookie}; {antiforgeryCookie}");
        legitimateRequest.Headers.Add("X-XSRF-TOKEN", token);
        var legitimateResponse = await _client.SendAsync(legitimateRequest);

        // Assert 3 — Legitimate request succeeds
        Assert.Equal(HttpStatusCode.OK, legitimateResponse.StatusCode);
    }

    [Fact]
    public async Task AdminLogin_AccountLockout_AfterRepeatedFailedAttempts()
    {
        // Arrange
        var email = $"lockout_{Guid.NewGuid():N}@test.local";
        var correctPass = "CorrectPass123!";
        await CreateTestUserAsync(email, correctPass, AppRoles.Admin);

        // Act — fail 4 consecutive times (returns 401 Unauthorized)
        for (int i = 0; i < 4; i++)
        {
            var failedResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
            {
                Email = email,
                Password = "WrongPass999!"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failedResponse.StatusCode);
        }

        // Attempt #5 triggers lockout (MaxFailedAccessAttempts = 5)
        var lockedResponse = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = "WrongPass999!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Locked, lockedResponse.StatusCode);
        var result = await lockedResponse.Content.ReadFromJsonAsync<ApiResponse<AdminUserDto>>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("locked out", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminSeeder_DoesNotOverwrite_ExistingAdministrator()
    {
        // Arrange — create an admin with a known password
        var existingEmail = $"original_admin_{Guid.NewGuid():N}@test.local";
        var originalPass = "OriginalPass123!";
        await CreateTestUserAsync(existingEmail, originalPass, AppRoles.Admin);

        // Attempt to run seeder with different credentials
        using var scope = _factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IAdminSeeder>();
        await seeder.SeedAsync();

        // Verify the original administrator still exists and can authenticate
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(existingEmail);
        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user, originalPass));
    }

    [Fact]
    public async Task ExistingEndpoints_RemainFullyFunctionalWithoutAuth()
    {
        // Health endpoint must remain functional
        var healthResponse = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        // Public categories endpoint must remain public
        var categoriesResponse = await _client.GetAsync("/api/categories");
        Assert.Equal(HttpStatusCode.OK, categoriesResponse.StatusCode);

        // Public products endpoint must remain public
        var productsResponse = await _client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, productsResponse.StatusCode);
    }
}
