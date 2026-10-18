using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Models.DTOs;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 6B-5A2: Secure Read-Only Admin Payments & Wallet APIs.
/// Verifies:
/// 1. Payments: Anonymous and customer requests are strictly rejected (401/403).
/// 2. Payments: Admin can list payments with server-side pagination, determinism, and multi-criteria filters.
/// 3. Payments: Both OrderPurchase and WalletTopUp payments appear with safe DTOs and customer links.
/// 4. Payments: Search by payment number, customer name, customer email, and TID works.
/// 5. Payments: General payment detail endpoint returns correct data; 404 on missing payment.
/// 6. Payments: Data privacy — sensitive secrets, API keys, passwords, and raw accounts are never exposed.
/// 7. Payments: Existing manual payment review routes (/admin/payments/manual and /admin/payments/manual/{pNumber}) continue working unchanged.
/// 8. Wallets: Anonymous and customer requests are strictly rejected (401/403).
/// 9. Wallets: Admin can list existing customer wallets with exact stored authoritative balances.
/// 10. Wallets: Pure read-only — viewing wallets never creates wallets or ledger entries.
/// 11. Wallets: Ledger endpoint retrieves wallet-specific transaction history with exact precision and balance snapshots.
/// 12. Wallets: Missing wallet returns 404 Not Found.
/// 13. Validation: Rejection of negative/zero page numbers, excessive page sizes (>100), invalid enums, and invalid identifiers with 400 Bad Request.
/// </summary>
public class AdminFinancialReadOnlyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AdminFinancialReadOnlyTests(CustomWebApplicationFactory factory)
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

    private async Task<(string CustomerId, string AuthCookie)> RegisterAndLoginCustomerAsync(
        string email,
        string password,
        string fullName = "Financial Customer")
    {
        await EnsureRolesExistAsync();

        var regRes = await _client.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = fullName
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(regData?.Data);

        var loginRes = await _client.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        return (regData.Data.Id, authCookie);
    }

    private async Task<(string AdminId, string AuthCookie)> LoginAsAdminAsync()
    {
        await EnsureRolesExistAsync();
        var email = $"admin_fin_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPassword123!";
        string adminId;

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = "Admin Financial Auditor",
                EmailConfirmed = true,
                IsActive = true
            };
            var createRes = await userManager.CreateAsync(user, password);
            Assert.True(createRes.Succeeded);
            await userManager.AddToRoleAsync(user, AppRoles.Admin);
            adminId = user.Id;
        }

        var loginRes = await _client.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        return (adminId, authCookie);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PAYMENTS: AUTHENTICATION & AUTHORIZATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPayments_Anonymous_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/admin/payments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPaymentDetail_Anonymous_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/admin/payments/PAY-20261018-000001");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPayments_CustomerAuthenticated_ReturnsForbiddenOrUnauthorized()
    {
        var (_, customerCookie) = await RegisterAndLoginCustomerAsync(
            $"cust_pay_block_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments");
        request.Headers.Add("Cookie", customerCookie);

        var response = await _client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401 or 403, but received {response.StatusCode}");
    }

    [Fact]
    public async Task GetPaymentDetail_CustomerAuthenticated_ReturnsForbiddenOrUnauthorized()
    {
        var (_, customerCookie) = await RegisterAndLoginCustomerAsync(
            $"cust_pay_det_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/PAY-20261018-000001");
        request.Headers.Add("Cookie", customerCookie);

        var response = await _client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401 or 403, but received {response.StatusCode}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PAYMENTS: LISTING, FILTERING, SEARCH & PAGINATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPayments_AdminAuthenticated_ReturnsBothOrderPurchaseAndWalletTopUp()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var (customerId, _) = await RegisterAndLoginCustomerAsync(
            $"cust_both_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!",
            "Both Types Customer");

        // Seed an OrderPurchase payment and a WalletTopUp payment directly in test DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var order = new Order
            {
                OrderNumber = $"ORD-TEST-{Guid.NewGuid():N}"[..20],
                CustomerId = customerId,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                TotalAmount = 1500.0000m
            };
            db.Orders.Add(order);

            var wallet = new Wallet
            {
                CustomerId = customerId,
                Currency = "PKR",
                Balance = 5000.0000m
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();

            var orderPayment = new Payment
            {
                PaymentNumber = $"PAY-ORD-{Guid.NewGuid():N}"[..20],
                Purpose = PaymentPurpose.OrderPurchase,
                OrderId = order.Id,
                Amount = 1500.0000m,
                Currency = "PKR",
                PaymentMethodCode = "WALLET",
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow
            };

            var topUpPayment = new Payment
            {
                PaymentNumber = $"PAY-TOP-{Guid.NewGuid():N}"[..20],
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                Amount = 5000.0000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending
            };

            db.Payments.AddRange(orderPayment, topUpPayment);
            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(result?.Data);
        Assert.True(result.Success);
        Assert.Contains(result.Data.Items, p => p.Purpose == "OrderPurchase" && p.OrderNumber != null);
        Assert.Contains(result.Data.Items, p => p.Purpose == "WalletTopUp" && p.WalletId != null);
    }

    [Fact]
    public async Task GetPayments_PurposeFilter_ReturnsFilteredResults()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        // 1. Filter by OrderPurchase
        var reqOrder = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments?purpose=OrderPurchase");
        reqOrder.Headers.Add("Cookie", adminCookie);
        var resOrder = await _client.SendAsync(reqOrder);
        Assert.Equal(HttpStatusCode.OK, resOrder.StatusCode);
        var dataOrder = await resOrder.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(dataOrder?.Data);
        Assert.All(dataOrder.Data.Items, p => Assert.Equal("OrderPurchase", p.Purpose));

        // 2. Filter by WalletTopUp
        var reqTopUp = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments?purpose=WalletTopUp");
        reqTopUp.Headers.Add("Cookie", adminCookie);
        var resTopUp = await _client.SendAsync(reqTopUp);
        Assert.Equal(HttpStatusCode.OK, resTopUp.StatusCode);
        var dataTopUp = await resTopUp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(dataTopUp?.Data);
        Assert.All(dataTopUp.Data.Items, p => Assert.Equal("WalletTopUp", p.Purpose));
    }

    [Fact]
    public async Task GetPayments_StatusFilter_ReturnsMatchingStatusOnly()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments?status=Paid");
        request.Headers.Add("Cookie", adminCookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(result?.Data);
        Assert.All(result.Data.Items, p => Assert.Equal("Paid", p.Status));
    }

    [Fact]
    public async Task GetPayments_PaymentMethodFilter_ReturnsMatchingMethodOnly()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments?paymentMethod=WALLET");
        request.Headers.Add("Cookie", adminCookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(result?.Data);
        Assert.All(result.Data.Items, p => Assert.Equal("WALLET", p.PaymentMethodCode));
    }

    [Fact]
    public async Task GetPayments_Search_MatchesSupportedFields()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var uniqueTid = $"TID-SRCH-{Guid.NewGuid():N}"[..18];
        var uniquePayNum = $"PAY-SRCH-{Guid.NewGuid():N}"[..18];
        var uniqueEmail = $"search_target_{Guid.NewGuid():N}@test.local";
        var (customerId, _) = await RegisterAndLoginCustomerAsync(uniqueEmail, "CustomerPass123!", "Specific Search User");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = new Wallet
            {
                CustomerId = customerId,
                Currency = "PKR",
                Balance = 1000m
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();

            var payment = new Payment
            {
                PaymentNumber = uniquePayNum,
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                Amount = 1000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Paid,
                ProviderTransactionId = uniqueTid
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        // Search by payment number
        var req1 = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments?search={uniquePayNum}");
        req1.Headers.Add("Cookie", adminCookie);
        var res1 = await _client.SendAsync(req1);
        var data1 = await res1.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(data1?.Data);
        Assert.Single(data1.Data.Items);
        Assert.Equal(uniquePayNum, data1.Data.Items[0].PaymentNumber);

        // Search by customer email
        var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments?search={uniqueEmail}");
        req2.Headers.Add("Cookie", adminCookie);
        var res2 = await _client.SendAsync(req2);
        var data2 = await res2.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(data2?.Data);
        Assert.Contains(data2.Data.Items, p => p.PaymentNumber == uniquePayNum);

        // Search by transaction reference (TID)
        var req3 = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments?search={uniqueTid}");
        req3.Headers.Add("Cookie", adminCookie);
        var res3 = await _client.SendAsync(req3);
        var data3 = await res3.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(data3?.Data);
        Assert.Contains(data3.Data.Items, p => p.PaymentNumber == uniquePayNum);
    }

    [Fact]
    public async Task GetPayments_Pagination_ReturnsCorrectTotalsAndRecords()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments?page=1&pageSize=2");
        request.Headers.Add("Cookie", adminCookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(result?.Data);
        Assert.Equal(1, result.Data.Page);
        Assert.Equal(2, result.Data.PageSize);
        Assert.True(result.Data.Items.Count <= 2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PAYMENTS: DETAILS & DATA PRIVACY
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPaymentDetail_ValidPayment_ReturnsCorrectDataAndMasksAccount()
    {
        var (adminId, adminCookie) = await LoginAsAdminAsync();
        var uniquePayNum = $"PAY-DET-{Guid.NewGuid():N}"[..18];
        var (customerId, _) = await RegisterAndLoginCustomerAsync(
            $"cust_det_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!",
            "Detail Customer");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = new Wallet
            {
                CustomerId = customerId,
                Currency = "PKR",
                Balance = 2500.0000m
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();

            var payment = new Payment
            {
                PaymentNumber = uniquePayNum,
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                Amount = 2500.0000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Paid,
                PaidAt = DateTime.UtcNow,
                ProviderTransactionId = "TID-987654321",
                CustomerReference = "CUST-REF-1122",
                CustomerSenderAccount = "03001234567", // Sensitive account to be masked
                CustomerNotes = "Transfer via JazzCash",
                CustomerProofSubmittedAt = DateTime.UtcNow.AddMinutes(-30),
                VerifiedByUserId = adminId,
                VerifiedReceivingAccount = "01020304050607", // Sensitive receiving account to be masked
                AdminNotes = "Confirmed receipt",
                VerifiedAt = DateTime.UtcNow
            };
            payment.StatusHistory.Add(new PaymentStatusHistory
            {
                PreviousStatus = PaymentStatus.Pending,
                NewStatus = PaymentStatus.Paid,
                Reason = "Approved by admin",
                ChangedByUserId = adminId,
                ChangedAt = DateTime.UtcNow
            });
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/{uniquePayNum}");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResponse<AdminPaymentDetailDto>>(rawJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(result?.Data);
        Assert.True(result.Success);
        Assert.Equal(uniquePayNum, result.Data.PaymentNumber);
        Assert.Equal("WalletTopUp", result.Data.Purpose);
        Assert.Equal(2500.0000m, result.Data.Amount);
        Assert.Equal("PKR", result.Data.Currency);
        Assert.Equal("Paid", result.Data.Status);
        Assert.Equal("TID-987654321", result.Data.ProviderTransactionId);

        // Verify account minimization
        Assert.Equal("***4567", result.Data.CustomerSenderAccount);
        Assert.DoesNotContain("03001234567", rawJson); // Full customer account must not appear in JSON
        Assert.Equal("***0607", result.Data.VerifiedReceivingAccount);
        Assert.DoesNotContain("01020304050607", rawJson); // Full receiving account must not appear in JSON

        // Verify secret leakage assertion
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vendorCost", rawJson, StringComparison.OrdinalIgnoreCase);

        // Verify status history
        Assert.NotEmpty(result.Data.StatusHistory);
        Assert.Equal("Paid", result.Data.StatusHistory[0].NewStatus);
    }

    [Fact]
    public async Task GetPaymentDetail_MissingPayment_Returns404()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/PAY-NONEXISTENT-999");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PAYMENTS: ROUTE RESOLUTION COMPATIBILITY (MANUAL PAYMENTS PRESERVED)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteResolution_ManualPaymentsRoute_ResolvesToManualPaymentsController()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        // 1. General admin payments
        var genReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments");
        genReq.Headers.Add("Cookie", adminCookie);
        var genRes = await _client.SendAsync(genReq);
        Assert.Equal(HttpStatusCode.OK, genRes.StatusCode);
        var genData = await genRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminPaymentSummaryDto>>>();
        Assert.NotNull(genData);
        Assert.Equal("Payments retrieved successfully.", genData.Message);

        // 2. Existing manual payments overview
        var manReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/manual");
        manReq.Headers.Add("Cookie", adminCookie);
        var manRes = await _client.SendAsync(manReq);
        Assert.Equal(HttpStatusCode.OK, manRes.StatusCode);
        var manData = await manRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminManualPaymentDto>>>();
        Assert.NotNull(manData);
        Assert.Equal("Manual payments retrieved successfully.", manData.Message);
    }

    [Fact]
    public async Task RouteResolution_ManualPaymentDetailRoute_ResolvesCorrectly()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var uniquePayNum = $"PAY-MAN-{Guid.NewGuid():N}"[..18];
        var (customerId, _) = await RegisterAndLoginCustomerAsync(
            $"cust_man_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = new Wallet
            {
                CustomerId = customerId,
                Currency = "PKR",
                Balance = 0m
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();

            var payment = new Payment
            {
                PaymentNumber = uniquePayNum,
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                Amount = 1200m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending
            };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
        }

        // Test GET /api/admin/payments/manual/{paymentNumber}
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/manual/{uniquePayNum}");
        request.Headers.Add("Cookie", adminCookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = await response.Content.ReadFromJsonAsync<ApiResponse<AdminManualPaymentDto>>();
        Assert.NotNull(data?.Data);
        Assert.Equal("Manual payment details retrieved successfully.", data.Message);
        Assert.Equal(uniquePayNum, data.Data.PaymentNumber);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WALLETS: AUTHENTICATION & AUTHORIZATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWallets_Anonymous_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/admin/wallets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetWalletTransactions_Anonymous_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/admin/wallets/1/transactions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetWallets_CustomerAuthenticated_ReturnsForbiddenOrUnauthorized()
    {
        var (_, customerCookie) = await RegisterAndLoginCustomerAsync(
            $"cust_wall_block_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/wallets");
        request.Headers.Add("Cookie", customerCookie);

        var response = await _client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401 or 403, but received {response.StatusCode}");
    }

    [Fact]
    public async Task GetWalletTransactions_CustomerAuthenticated_ReturnsForbiddenOrUnauthorized()
    {
        var (_, customerCookie) = await RegisterAndLoginCustomerAsync(
            $"cust_tx_block_{Guid.NewGuid():N}@test.local",
            "CustomerPass123!");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/wallets/1/transactions");
        request.Headers.Add("Cookie", customerCookie);

        var response = await _client.SendAsync(request);
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected 401 or 403, but received {response.StatusCode}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WALLETS: LISTING, AUTHORITATIVE BALANCE, SEARCH & NO MUTATIONS
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWallets_AdminAuthenticated_ReturnsAuthoritativeStoredBalances()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var uniqueEmail = $"wallet_user_{Guid.NewGuid():N}@test.local";
        var (customerId, _) = await RegisterAndLoginCustomerAsync(uniqueEmail, "CustomerPass123!", "Authoritative User");

        const decimal authoritativeBalance = 7890.1234m;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = new Wallet
            {
                CustomerId = customerId,
                Currency = "PKR",
                Balance = authoritativeBalance
            };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/wallets?search={uniqueEmail}");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminWalletSummaryDto>>>();
        Assert.NotNull(result?.Data);
        Assert.Single(result.Data.Items);

        var walletDto = result.Data.Items[0];
        Assert.Equal(customerId, walletDto.CustomerId);
        Assert.Equal("Authoritative User", walletDto.CustomerFullName);
        Assert.Equal(uniqueEmail, walletDto.CustomerEmail);
        Assert.Equal(authoritativeBalance, walletDto.CurrentBalance);
        Assert.Equal("PKR", walletDto.Currency);
    }

    [Fact]
    public async Task GetWallets_PureReadOperation_DoesNotCreateOrMutateWallets()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        int initialWalletCount;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            initialWalletCount = await db.Wallets.CountAsync();
        }

        // Send GET request for non-existent customer
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/wallets?search=nonexistent_user@test.local");
        request.Headers.Add("Cookie", adminCookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var postWalletCount = await db.Wallets.CountAsync();
            Assert.Equal(initialWalletCount, postWalletCount);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WALLETS: TRANSACTION LEDGER & ISOLATION
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWalletTransactions_ReturnsWalletSpecificLedgerEntriesWithExactSnapshots()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        long walletAId;
        long walletBId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var custA = new ApplicationUser { UserName = $"custA_{Guid.NewGuid():N}@test.local", Email = $"custA_{Guid.NewGuid():N}@test.local", FullName = "Cust A" };
            var custB = new ApplicationUser { UserName = $"custB_{Guid.NewGuid():N}@test.local", Email = $"custB_{Guid.NewGuid():N}@test.local", FullName = "Cust B" };
            db.Users.AddRange(custA, custB);
            await db.SaveChangesAsync();

            var walletA = new Wallet { CustomerId = custA.Id, Currency = "PKR", Balance = 1500m };
            var walletB = new Wallet { CustomerId = custB.Id, Currency = "PKR", Balance = 3000m };
            db.Wallets.AddRange(walletA, walletB);
            await db.SaveChangesAsync();

            walletAId = walletA.Id;
            walletBId = walletB.Id;

            // Transactions for Wallet A
            db.WalletTransactions.AddRange(
                new WalletTransaction
                {
                    WalletId = walletAId,
                    TransactionType = WalletTransactionType.Credit,
                    Amount = 2000.0000m,
                    Currency = "PKR",
                    BalanceBefore = 0.0000m,
                    BalanceAfter = 2000.0000m,
                    OperationKey = $"OP-A1-{Guid.NewGuid():N}",
                    SourceType = WalletTransactionSource.DepositVerification,
                    Description = "Initial deposit",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10)
                },
                new WalletTransaction
                {
                    WalletId = walletAId,
                    TransactionType = WalletTransactionType.Debit,
                    Amount = 500.0000m,
                    Currency = "PKR",
                    BalanceBefore = 2000.0000m,
                    BalanceAfter = 1500.0000m,
                    OperationKey = $"OP-A2-{Guid.NewGuid():N}",
                    SourceType = WalletTransactionSource.OrderPayment,
                    SourceReferenceId = "ORD-TEST-1001",
                    Description = "Order purchase",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5)
                }
            );

            // Transaction for Wallet B
            db.WalletTransactions.Add(new WalletTransaction
            {
                WalletId = walletBId,
                TransactionType = WalletTransactionType.Credit,
                Amount = 3000.0000m,
                Currency = "PKR",
                BalanceBefore = 0.0000m,
                BalanceAfter = 3000.0000m,
                OperationKey = $"OP-B1-{Guid.NewGuid():N}",
                SourceType = WalletTransactionSource.DepositVerification,
                Description = "Wallet B deposit",
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }

        // Query Wallet A's transactions
        var reqA = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/wallets/{walletAId}/transactions");
        reqA.Headers.Add("Cookie", adminCookie);
        var resA = await _client.SendAsync(reqA);
        Assert.Equal(HttpStatusCode.OK, resA.StatusCode);

        var dataA = await resA.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminWalletTransactionDto>>>();
        Assert.NotNull(dataA?.Data);
        Assert.Equal(2, dataA.Data.TotalCount);

        // Strict isolation: Wallet A must contain ONLY Wallet A transactions
        Assert.All(dataA.Data.Items, t => Assert.Equal(walletAId, t.WalletId));
        Assert.DoesNotContain(dataA.Data.Items, t => t.WalletId == walletBId);

        // Verify exact balance snapshots
        var debitTx = dataA.Data.Items.First(t => t.TransactionType == "Debit");
        Assert.Equal(500.0000m, debitTx.Amount);
        Assert.Equal(2000.0000m, debitTx.BalanceBefore);
        Assert.Equal(1500.0000m, debitTx.BalanceAfter);
        Assert.Equal("OrderPayment", debitTx.SourceType);
        Assert.Equal("ORD-TEST-1001", debitTx.SourceReferenceId);

        var creditTx = dataA.Data.Items.First(t => t.TransactionType == "Credit");
        Assert.Equal(2000.0000m, creditTx.Amount);
        Assert.Equal(0.0000m, creditTx.BalanceBefore);
        Assert.Equal(2000.0000m, creditTx.BalanceAfter);
        Assert.Equal("DepositVerification", creditTx.SourceType);
    }

    [Fact]
    public async Task GetWalletTransactions_MissingWallet_Returns404()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/wallets/999999/transactions");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWalletTransactions_Filters_ReturnsFilteredLedger()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        long walletId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cust = new ApplicationUser { UserName = $"cust_filter_{Guid.NewGuid():N}@test.local", Email = $"cust_filter_{Guid.NewGuid():N}@test.local" };
            db.Users.Add(cust);
            await db.SaveChangesAsync();

            var wallet = new Wallet { CustomerId = cust.Id, Currency = "PKR", Balance = 100m };
            db.Wallets.Add(wallet);
            await db.SaveChangesAsync();
            walletId = wallet.Id;

            db.WalletTransactions.AddRange(
                new WalletTransaction
                {
                    WalletId = walletId,
                    TransactionType = WalletTransactionType.Credit,
                    Amount = 200m,
                    Currency = "PKR",
                    BalanceBefore = 0m,
                    BalanceAfter = 200m,
                    OperationKey = $"OP-F1-{Guid.NewGuid():N}",
                    SourceType = WalletTransactionSource.DepositVerification
                },
                new WalletTransaction
                {
                    WalletId = walletId,
                    TransactionType = WalletTransactionType.Debit,
                    Amount = 100m,
                    Currency = "PKR",
                    BalanceBefore = 200m,
                    BalanceAfter = 100m,
                    OperationKey = $"OP-F2-{Guid.NewGuid():N}",
                    SourceType = WalletTransactionSource.OrderPayment
                }
            );
            await db.SaveChangesAsync();
        }

        // Filter by Debit
        var reqDebit = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/wallets/{walletId}/transactions?transactionType=Debit");
        reqDebit.Headers.Add("Cookie", adminCookie);
        var resDebit = await _client.SendAsync(reqDebit);
        Assert.Equal(HttpStatusCode.OK, resDebit.StatusCode);
        var dataDebit = await resDebit.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminWalletTransactionDto>>>();
        Assert.NotNull(dataDebit?.Data);
        Assert.Single(dataDebit.Data.Items);
        Assert.Equal("Debit", dataDebit.Data.Items[0].TransactionType);

        // Filter by SourceType
        var reqSource = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/wallets/{walletId}/transactions?sourceType=DepositVerification");
        reqSource.Headers.Add("Cookie", adminCookie);
        var resSource = await _client.SendAsync(reqSource);
        Assert.Equal(HttpStatusCode.OK, resSource.StatusCode);
        var dataSource = await resSource.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminWalletTransactionDto>>>();
        Assert.NotNull(dataSource?.Data);
        Assert.Single(dataSource.Data.Items);
        Assert.Equal("DepositVerification", dataSource.Data.Items[0].SourceType);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NEGATIVE TESTS: INVALID FILTERS, PAGINATION LIMITS, AND IDENTIFIERS
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/admin/payments?page=0")]
    [InlineData("/api/admin/payments?page=-1")]
    [InlineData("/api/admin/payments?pageSize=0")]
    [InlineData("/api/admin/payments?pageSize=101")]
    [InlineData("/api/admin/payments?paymentMethod=NON_EXISTENT_METHOD")]
    [InlineData("/api/admin/wallets?page=0")]
    [InlineData("/api/admin/wallets?pageSize=101")]
    [InlineData("/api/admin/wallets/0/transactions")]
    [InlineData("/api/admin/wallets/-1/transactions")]
    [InlineData("/api/admin/wallets/1/transactions?page=0")]
    [InlineData("/api/admin/wallets/1/transactions?pageSize=101")]
    [InlineData("/api/admin/wallets/1/transactions?sourceType=INVALID_SOURCE_TYPE")]
    public async Task NegativeValidation_InvalidParameters_ReturnBadRequest(string url)
    {
        var (_, adminCookie) = await LoginAsAdminAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(error);
        Assert.False(error.Success);
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public async Task NegativeValidation_ExcessiveSearchLength_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var longSearch = new string('x', 101);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments?search={longSearch}");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NegativeValidation_ExcessivePaymentNumberLength_ReturnsBadRequest()
    {
        var (_, adminCookie) = await LoginAsAdminAsync();
        var longPayNum = new string('a', 51);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/{longPayNum}");
        request.Headers.Add("Cookie", adminCookie);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
