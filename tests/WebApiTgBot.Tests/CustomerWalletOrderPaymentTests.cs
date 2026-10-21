using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Identity.Models.DTOs;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;
using WebApiTgBot.Modules.Orders.Services;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Payments.Services;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Models.DTOs;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 4D: Customer Wallet Purchases & Central Order Payment Processing.
/// Verifies:
/// 1. Feature gate safety (disabled by default in production -> 403 Forbidden).
/// 2. Authentication & Authorization enforcement (unauthenticated, deactivated, cross-customer order isolation).
/// 3. Balance verification and insufficient funds handling (order and wallet untouched).
/// 4. Successful atomic wallet payment (order -> Paid, payment -> Paid, wallet -> Debited, audit histories recorded).
/// 5. Durable idempotency guarantees (same key returns original confirmation, different key on paid order fails).
/// 6. Read-only customer wallet endpoints (balance inquiry, paginated transactions, privacy isolation).
/// 7. PostgreSQL database-level constraints (IX_Payments_Paid_OrderId partial unique index).
/// 8. Real PostgreSQL concurrency safety:
///    - Concurrent duplicate payment of same order (exactly 1 succeeds, 1 conflict, 1 debit).
///    - Concurrent different orders competing for insufficient balance (Scenario A: exactly 1 succeeds, other fails).
/// 9. Full transactional rollback on unexpected errors.
/// 10. Separation between WalletTopUp and OrderPurchase payment flows.
/// </summary>
public class CustomerWalletOrderPaymentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CustomerWalletOrderPaymentTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpClient CreateEnabledClient()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
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
        HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;

        var regRes = await httpClient.PostAsJsonAsync("/api/customer/auth/register", new CustomerRegisterRequest
        {
            Email = email,
            Password = password,
            FullName = "Wallet Order Customer"
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        var customerId = regData!.Data!.Id;

        var loginRes = await httpClient.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Customer.Auth"))
            .Split(';')[0];

        return (customerId, authCookie);
    }

    private async Task<(string CsrfToken, string AntiforgeryCookie)> GetCustomerCsrfTokenAsync(
        string authCookie,
        HttpClient? client = null)
    {
        var httpClient = client ?? _client;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        req.Headers.Add("Cookie", authCookie);
        var res = await httpClient.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<string>>();
        var csrfToken = body!.Data!;

        var antiCookie = string.Empty;
        if (res.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var match = cookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiCookie = match.Split(';')[0];
            }
        }

        return (csrfToken, antiCookie);
    }

    private async Task<Product> CreateTestProductAsync(string sku, decimal price)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var catSlug = $"cat_{Guid.NewGuid():N}";
        var category = new Category
        {
            Name = "Category " + catSlug,
            Slug = catSlug,
            IsActive = true
        };
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            InternalSku = sku,
            Name = "Product " + sku,
            SellingPrice = price,
            SellingCurrency = "PKR",
            CategoryId = category.Id,
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product;
    }

    private async Task<string> CreateCustomerOrderAsync(
        string sku,
        string authCookie,
        string csrfToken,
        string antiCookie,
        HttpClient client)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders")
        {
            Content = JsonContent.Create(new CreateCustomerOrderRequest
            {
                ProductSku = sku,
                CustomerNotes = "Test order for wallet payment"
            })
        };
        req.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req.Headers.Add("X-Idempotency-Key", $"ORD-KEY-{Guid.NewGuid():N}");

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var data = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderDetailDto>>();
        return data!.Data!.OrderNumber;
    }

    private async Task CreditCustomerWalletAsync(string customerId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IWalletService>();
        var result = await walletService.CreditAsync(new WalletCreditRequest
        {
            CustomerId = customerId,
            Amount = amount,
            Currency = "PKR",
            OperationKey = $"TEST_SEED_CREDIT_{Guid.NewGuid():N}",
            SourceType = WalletTransactionSource.DepositVerification,
            Description = "Test initial seed credit"
        });
        Assert.True(result.IsSuccess);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Feature Gate Safety
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_FeatureGateDisabledByDefault_ReturnsForbidden()
    {
        // Use default client where WalletPurchaseOptions.Enabled == false
        var client = _client;
        var email = $"gate_ord_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-GATE-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.Forbidden, payRes.StatusCode);

        // Verify order remains PendingPayment
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.FirstAsync(o => o.OrderNumber == orderNumber);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. Authentication & Authorization Enforcement
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_Unauthenticated_ReturnsUnauthorized()
    {
        var client = CreateEnabledClient();
        var payReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/orders/ORD-NONEXISTENT/pay-with-wallet");
        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.Unauthorized, payRes.StatusCode);
    }

    [Fact]
    public async Task PayWithWallet_DifferentCustomerOrder_ReturnsNotFound()
    {
        var client = CreateEnabledClient();

        // Customer A creates order
        var emailA = $"custA_{Guid.NewGuid():N}@test.local";
        var (custAId, authA) = await RegisterAndLoginCustomerAsync(emailA, "Password123!", client);
        var (csrfA, antiA) = await GetCustomerCsrfTokenAsync(authA, client);
        var sku = $"SKU-ISO-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 2000m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authA, csrfA, antiA, client);

        // Customer B tries to pay Customer A's order
        var emailB = $"custB_{Guid.NewGuid():N}@test.local";
        var (custBId, authB) = await RegisterAndLoginCustomerAsync(emailB, "Password123!", client);
        var (csrfB, antiB) = await GetCustomerCsrfTokenAsync(authB, client);
        await CreditCustomerWalletAsync(custBId, 5000m);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiB) ? authB : $"{authB}; {antiB}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfB);

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.NotFound, payRes.StatusCode);

        // Verify order remains PendingPayment and Customer B wallet untouched
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.FirstAsync(o => o.OrderNumber == orderNumber);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);

        var walletB = await db.Wallets.FirstAsync(w => w.CustomerId == custBId);
        Assert.Equal(5000.0000m, walletB.Balance);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. Balance Verification & Insufficient Funds
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_InsufficientFunds_ReturnsBadRequest_OrderAndWalletUntouched()
    {
        var client = CreateEnabledClient();
        var email = $"insuf_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-INSUF-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 3000m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        // Credit customer wallet with only 1,000 PKR (required: 3,000 PKR)
        await CreditCustomerWalletAsync(customerId, 1000m);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.BadRequest, payRes.StatusCode);

        var body = await payRes.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        Assert.Contains("Insufficient wallet balance", body!.Message);

        // Verify database: Order is STILL PendingPayment, wallet balance remains 1,000 PKR, zero payments created
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.FirstAsync(o => o.OrderNumber == orderNumber);
        Assert.Equal(OrderStatus.PendingPayment, order.Status);

        var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
        Assert.Equal(1000.0000m, wallet.Balance);

        var paymentCount = await db.Payments.CountAsync(p => p.OrderId == order.Id);
        Assert.Equal(0, paymentCount);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. Successful Wallet Purchase & Atomic State Synchronization
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_SuccessfulPurchase_DeductsWallet_AdvancesOrderAndPaymentToPaid_RecordsAuditTrail()
    {
        var client = CreateEnabledClient();
        var email = $"success_pay_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-SUCCESS-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 2500m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        // Fund customer wallet with 6,000 PKR
        await CreditCustomerWalletAsync(customerId, 6000m);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.OK, payRes.StatusCode);

        var body = await payRes.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        Assert.NotNull(body?.Data);
        var paymentDto = body.Data;

        Assert.Equal(orderNumber, paymentDto.OrderNumber);
        Assert.Equal("Paid", paymentDto.OrderStatus);
        Assert.Equal("Paid", paymentDto.PaymentStatus);
        Assert.Equal(2500m, paymentDto.AmountPaid);
        Assert.Equal("PKR", paymentDto.Currency);
        Assert.Equal("WALLET", paymentDto.PaymentMethod);
        Assert.Equal(3500.0000m, paymentDto.WalletRemainingBalance); // 6000 - 2500 = 3500
        Assert.StartsWith("PAY-", paymentDto.PaymentNumber);

        // Verify database: Order, OrderStatusHistory, Payment, PaymentStatusHistory, Wallet, WalletTransaction
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Order status
            var order = await db.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.OrderNumber == orderNumber);
            Assert.Equal(OrderStatus.Paid, order.Status);
            Assert.Contains(order.StatusHistory, h => h.PreviousStatus == OrderStatus.PendingPayment && h.NewStatus == OrderStatus.Paid);

            // 2. Payment record
            var payment = await db.Payments
                .Include(p => p.StatusHistory)
                .FirstAsync(p => p.PaymentNumber == paymentDto.PaymentNumber);
            Assert.Equal(PaymentPurpose.OrderPurchase, payment.Purpose);
            Assert.Equal(order.Id, payment.OrderId);
            Assert.Null(payment.WalletId); // Payment targets Order, not Wallet
            Assert.Equal(2500m, payment.Amount);
            Assert.Equal("PKR", payment.Currency);
            Assert.Equal("WALLET", payment.PaymentMethodCode);
            Assert.Equal(PaymentStatus.Paid, payment.Status);
            Assert.NotNull(payment.PaidAt);
            Assert.StartsWith("WALLET:", payment.ProviderTransactionId);

            // 3. Payment status history
            Assert.Equal(2, payment.StatusHistory.Count);
            Assert.Contains(payment.StatusHistory, h => h.PreviousStatus == null && h.NewStatus == PaymentStatus.Pending);
            Assert.Contains(payment.StatusHistory, h => h.PreviousStatus == PaymentStatus.Pending && h.NewStatus == PaymentStatus.Paid);

            // 4. Wallet balance & ledger entry
            var wallet = await db.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(3500.0000m, wallet.Balance);

            var debitTx = wallet.Transactions.FirstOrDefault(t => t.TransactionType == WalletTransactionType.Debit);
            Assert.NotNull(debitTx);
            Assert.Equal(2500m, debitTx.Amount);
            Assert.Equal(6000m, debitTx.BalanceBefore);
            Assert.Equal(3500m, debitTx.BalanceAfter);
            Assert.Equal(WalletTransactionSource.OrderPayment, debitTx.SourceType);
            Assert.Equal(orderNumber, debitTx.SourceReferenceId);
            Assert.Equal($"WALLET_ORDER:{order.Id}", debitTx.OperationKey);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. Durable Idempotency & Duplicate Payment Safety
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_IdempotencyKey_SameKeyReplay_ReturnsOriginalResultWithoutDoubleDebit()
    {
        var client = CreateEnabledClient();
        var email = $"idem_pay_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-IDEM-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1200m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        // Seed wallet with 5,000 PKR
        await CreditCustomerWalletAsync(customerId, 5000m);

        var sharedKey = $"IDEM-PAY-KEY-{Guid.NewGuid():N}";

        // Request 1: Initial Payment
        var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        req1.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req1.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req1.Headers.Add("X-Idempotency-Key", sharedKey);

        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var data1 = (await res1.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>())!.Data!;

        // Request 2: Replay identical request with SAME Idempotency Key
        var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        req2.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req2.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req2.Headers.Add("X-Idempotency-Key", sharedKey);

        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var data2 = (await res2.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>())!.Data!;

        // Must return identical payment details
        Assert.Equal(data1.PaymentNumber, data2.PaymentNumber);
        Assert.Equal(data1.AmountPaid, data2.AmountPaid);
        Assert.Equal(data1.WalletRemainingBalance, data2.WalletRemainingBalance);

        // Verify database: Balance remains 3,800 PKR (5000 - 1200), exactly ONE debit, exactly ONE Payment
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.Wallets.Include(w => w.Transactions).FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(3800.0000m, wallet.Balance);

            var debits = wallet.Transactions.Where(t => t.TransactionType == WalletTransactionType.Debit).ToList();
            Assert.Single(debits);

            var payments = await db.Payments.Where(p => p.OrderId == db.Orders.First(o => o.OrderNumber == orderNumber).Id).ToListAsync();
            Assert.Single(payments);
        }

        // Request 3: Submit DIFFERENT key for the already-paid order -> 409 Conflict
        var req3 = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        req3.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req3.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req3.Headers.Add("X-Idempotency-Key", $"DIFFERENT-KEY-{Guid.NewGuid():N}");

        var res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.Conflict, res3.StatusCode);

        // Request 4: Submit request with NO idempotency key for the already-paid order -> 409 Conflict
        var req4 = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        req4.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        req4.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var res4 = await client.SendAsync(req4);
        Assert.Equal(HttpStatusCode.Conflict, res4.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 6. Read-Only Customer Wallet Endpoints
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomerWallet_ReadOnlyEndpoints_GetWallet_And_GetTransactions()
    {
        var client = CreateEnabledClient();
        var email = $"read_wal_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);

        // Credit customer wallet with 1,500 PKR
        await CreditCustomerWalletAsync(customerId, 1500m);

        // 1. GET /api/customer/wallet
        var walReq = new HttpRequestMessage(HttpMethod.Get, "/api/customer/wallet");
        walReq.Headers.Add("Cookie", authCookie);
        var walRes = await client.SendAsync(walReq);
        Assert.Equal(HttpStatusCode.OK, walRes.StatusCode);

        var walBody = await walRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletDto>>();
        Assert.NotNull(walBody?.Data);
        Assert.Equal(1500.0000m, walBody.Data.Balance);
        Assert.Equal("PKR", walBody.Data.Currency);

        // 2. GET /api/customer/wallet/transactions
        var txReq = new HttpRequestMessage(HttpMethod.Get, "/api/customer/wallet/transactions?page=1&pageSize=10");
        txReq.Headers.Add("Cookie", authCookie);
        var txRes = await client.SendAsync(txReq);
        Assert.Equal(HttpStatusCode.OK, txRes.StatusCode);

        var txBody = await txRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<CustomerWalletTransactionDto>>>();
        Assert.NotNull(txBody?.Data);
        Assert.Single(txBody.Data.Items);
        var txDto = txBody.Data.Items.First();
        Assert.Equal("Credit", txDto.TransactionType);
        Assert.Equal(1500m, txDto.Amount);
        Assert.Equal("PKR", txDto.Currency);
        Assert.Equal(0m, txDto.BalanceBefore);
        Assert.Equal(1500m, txDto.BalanceAfter);
        Assert.Equal("DepositVerification", txDto.SourceType);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7. PostgreSQL Database-Level Constraint: IX_Payments_Paid_OrderId
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_UniquePaidOrderPaymentIndex_BlocksDoublePaidPaymentsAtEngineLevel()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var db = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(db))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_paid_idx_{suffix}@test.local",
            Email = $"pg_paid_idx_{suffix}@test.local",
            FullName = "PG Paid Index User",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);

        var order = new Order
        {
            OrderNumber = $"ORD-PG-IDX-{suffix}",
            CustomerId = customer.Id,
            Status = OrderStatus.Paid,
            Currency = "PKR",
            Subtotal = 2000m,
            TotalAmount = 2000m,
            SalesChannel = SalesChannel.Website
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var payment1 = new Payment
        {
            PaymentNumber = $"PAY-PG-1-{suffix}",
            Purpose = PaymentPurpose.OrderPurchase,
            OrderId = order.Id,
            Amount = 2000m,
            Currency = "PKR",
            PaymentMethodCode = "WALLET",
            Status = PaymentStatus.Paid,
            PaidAt = DateTime.UtcNow
        };
        db.Payments.Add(payment1);
        await db.SaveChangesAsync();

        // Attempt to add a SECOND Paid payment for the SAME order
        var payment2 = new Payment
        {
            PaymentNumber = $"PAY-PG-2-{suffix}",
            Purpose = PaymentPurpose.OrderPurchase,
            OrderId = order.Id,
            Amount = 2000m,
            Currency = "PKR",
            PaymentMethodCode = "WALLET",
            Status = PaymentStatus.Paid, // Prohibited by IX_Payments_Paid_OrderId!
            PaidAt = DateTime.UtcNow
        };
        db.Payments.Add(payment2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal("23505", pgEx.SqlState); // unique_violation
        Assert.Contains("IX_Payments_Paid_OrderId", pgEx.ConstraintName ?? pgEx.Message);

        // Cleanup
        await using var cleanDb = new AppDbContext(options);
        var p = await cleanDb.Payments.FirstOrDefaultAsync(x => x.PaymentNumber == payment1.PaymentNumber);
        if (p != null) cleanDb.Payments.Remove(p);
        var o = await cleanDb.Orders.FindAsync(order.Id);
        if (o != null) cleanDb.Orders.Remove(o);
        var u = await cleanDb.Users.FindAsync(customer.Id);
        if (u != null) cleanDb.Users.Remove(u);
        await cleanDb.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 8. PostgreSQL Concurrency: Two Simultaneous Payments for Same Order
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentDuplicatePayment_SameOrder_ExactlyOneDebitsAndSucceeds()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_dup_ord_{suffix}@test.local",
            Email = $"pg_dup_ord_{suffix}@test.local",
            FullName = "PG Dup Ord User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 5000.0000m // Seed with 5,000 PKR
        };
        seedDb.Wallets.Add(wallet);

        var orderNumber = $"ORD-CONC-DUP-{suffix}";
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1500m,
            TotalAmount = 1500m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(order);
        await seedDb.SaveChangesAsync();

        try
        {
            // Set up two distinct DbContexts and WalletOrderPaymentServices
            await using var db1 = new AppDbContext(options);
            await using var db2 = new AppDbContext(options);

            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });

            var walletService1 = new WalletService(db1, NullLogger<WalletService>.Instance);
            var paymentService1 = new WalletOrderPaymentService(
                db1,
                walletService1,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            var walletService2 = new WalletService(db2, NullLogger<WalletService>.Instance);
            var paymentService2 = new WalletOrderPaymentService(
                db2,
                walletService2,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            // Execute concurrent payments simultaneously
            var task1 = Task.Run(() => paymentService1.PayOrderWithWalletAsync(customer.Id, orderNumber, null));
            var task2 = Task.Run(() => paymentService2.PayOrderWithWalletAsync(customer.Id, orderNumber, null));

            var results = await Task.WhenAll(task1, task2);

            // Exactly ONE must succeed, exactly ONE must be Conflict
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Success);
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Conflict);

            // Verify final state in PostgreSQL:
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            // Balance must be exactly 3,500 PKR (5,000 - 1,500)
            Assert.Equal(3500.0000m, verifiedWallet.Balance);
            Assert.Single(verifiedWallet.Transactions);
            Assert.Equal(1500m, verifiedWallet.Transactions.First().Amount);
            Assert.Equal(WalletTransactionType.Debit, verifiedWallet.Transactions.First().TransactionType);

            var verifiedOrder = await verifyDb.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderStatus.Paid, verifiedOrder.Status);
            Assert.Single(verifiedOrder.StatusHistory, h => h.NewStatus == OrderStatus.Paid);

            var payments = await verifyDb.Payments.Where(p => p.OrderId == order.Id).ToListAsync();
            Assert.Single(payments);
            Assert.Equal(PaymentStatus.Paid, payments.First().Status);
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId == order.Id).ToListAsync();
            foreach (var p in pmts)
            {
                cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            }
            cleanDb.Payments.RemoveRange(pmts);

            var ord = await cleanDb.Orders.Include(o => o.StatusHistory).FirstOrDefaultAsync(o => o.Id == order.Id);
            if (ord != null)
            {
                cleanDb.OrderStatusHistories.RemoveRange(ord.StatusHistory);
                cleanDb.Orders.Remove(ord);
            }

            var wal = await cleanDb.Wallets.Include(w => w.Transactions).FirstOrDefaultAsync(w => w.Id == wallet.Id);
            if (wal != null)
            {
                cleanDb.WalletTransactions.RemoveRange(wal.Transactions);
                cleanDb.Wallets.Remove(wal);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);

            await cleanDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 9. PostgreSQL Concurrency: Two Different Orders Competing for Insufficient Balance (Scenario A)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentCompetingOrders_ScenarioA_ExactlyOneSucceeds_NoOverdraw()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_scen_a_{suffix}@test.local",
            Email = $"pg_scen_a_{suffix}@test.local",
            FullName = "PG Scenario A User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        // Balance: 1,000 PKR
        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 1000.0000m
        };
        seedDb.Wallets.Add(wallet);

        // Order A: 800 PKR
        var orderNumberA = $"ORD-SCENA-A-{suffix}";
        var orderA = new Order
        {
            OrderNumber = orderNumberA,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 800m,
            TotalAmount = 800m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(orderA);

        // Order B: 800 PKR
        var orderNumberB = $"ORD-SCENA-B-{suffix}";
        var orderB = new Order
        {
            OrderNumber = orderNumberB,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 800m,
            TotalAmount = 800m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(orderB);

        await seedDb.SaveChangesAsync();

        try
        {
            await using var dbA = new AppDbContext(options);
            await using var dbB = new AppDbContext(options);

            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });

            var walletServiceA = new WalletService(dbA, NullLogger<WalletService>.Instance);
            var paymentServiceA = new WalletOrderPaymentService(
                dbA,
                walletServiceA,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            var walletServiceB = new WalletService(dbB, NullLogger<WalletService>.Instance);
            var paymentServiceB = new WalletOrderPaymentService(
                dbB,
                walletServiceB,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            // Execute concurrent payments simultaneously
            var taskA = Task.Run(() => paymentServiceA.PayOrderWithWalletAsync(customer.Id, orderNumberA, null));
            var taskB = Task.Run(() => paymentServiceB.PayOrderWithWalletAsync(customer.Id, orderNumberB, null));

            var results = await Task.WhenAll(taskA, taskB);

            // Exactly ONE succeeds, exactly ONE fails due to InsufficientFunds
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Success);
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.InsufficientFunds);

            // Verify final state in PostgreSQL:
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            // Balance must be exactly 200 PKR (1,000 - 800)
            Assert.Equal(200.0000m, verifiedWallet.Balance);
            Assert.Single(verifiedWallet.Transactions);
            Assert.Equal(800m, verifiedWallet.Transactions.First().Amount);

            var ordA = await verifyDb.Orders.FirstAsync(o => o.Id == orderA.Id);
            var ordB = await verifyDb.Orders.FirstAsync(o => o.Id == orderB.Id);

            // One is Paid, the other is STILL PendingPayment
            var paidCount = (ordA.Status == OrderStatus.Paid ? 1 : 0) + (ordB.Status == OrderStatus.Paid ? 1 : 0);
            var pendingCount = (ordA.Status == OrderStatus.PendingPayment ? 1 : 0) + (ordB.Status == OrderStatus.PendingPayment ? 1 : 0);
            Assert.Equal(1, paidCount);
            Assert.Equal(1, pendingCount);
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId == orderA.Id || p.OrderId == orderB.Id).ToListAsync();
            foreach (var p in pmts)
            {
                cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            }
            cleanDb.Payments.RemoveRange(pmts);

            var orders = await cleanDb.Orders.Include(o => o.StatusHistory).Where(o => o.CustomerId == customer.Id).ToListAsync();
            foreach (var o in orders)
            {
                cleanDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            }
            cleanDb.Orders.RemoveRange(orders);

            var wal = await cleanDb.Wallets.Include(w => w.Transactions).FirstOrDefaultAsync(w => w.Id == wallet.Id);
            if (wal != null)
            {
                cleanDb.WalletTransactions.RemoveRange(wal.Transactions);
                cleanDb.Wallets.Remove(wal);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);

            await cleanDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 10. Pricing Snapshot Immutability: Catalog Price Change Does Not Affect Order Total
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_PriceChangedInCatalog_DebitsOriginalOrderSnapshotAmount()
    {
        var client = CreateEnabledClient();
        var email = $"snap_pay_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-SNAP-{Guid.NewGuid():N}"[..18];
        var product = await CreateTestProductAsync(sku, 2000m); // Originally 2,000 PKR
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        // Later: Catalog product price increases to 4,000 PKR
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Products.FirstAsync(x => x.InternalSku == sku);
            p.SellingPrice = 4000m;
            await db.SaveChangesAsync();
        }

        // Fund customer wallet with 3,000 PKR
        // If system erroneously used catalog price (4,000), it would fail with Insufficient Funds!
        // If system correctly uses stored order snapshot (2,000), it succeeds and leaves 1,000 PKR!
        await CreditCustomerWalletAsync(customerId, 3000m);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.OK, payRes.StatusCode);

        var body = await payRes.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        Assert.NotNull(body?.Data);
        Assert.Equal(2000m, body.Data.AmountPaid); // Debited original snapshot, NOT catalog price
        Assert.Equal(1000.0000m, body.Data.WalletRemainingBalance); // 3,000 - 2,000 = 1,000
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 11. Deactivated Customer Account Handling
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_DeactivatedCustomer_ReturnsUnauthorizedOrForbidden()
    {
        var client = CreateEnabledClient();
        var email = $"deact_pay_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var sku = $"SKU-DEACT-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);
        await CreditCustomerWalletAsync(customerId, 3000m);

        // Deactivate customer account
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FindAsync(customerId);
            user!.IsActive = false;
            await db.SaveChangesAsync();
        }

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var payRes = await client.SendAsync(payReq);
        // OnValidatePrincipal revokes deactivated sessions (401 or 403)
        Assert.True(payRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // Verify wallet balance is completely untouched
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(3000.0000m, wallet.Balance);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 12. Top-Up vs Order Purchase Purpose Separation & Mutual Exclusivity
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PayWithWallet_TopUpAndOrderPurchase_StrictlySeparated_PurposeConstraintsHold()
    {
        var client = CreateEnabledClient();
        var email = $"sep_chk_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        // 1. Create a Top-Up Payment
        var topUpReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 5000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        topUpReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        topUpReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
        var topUpRes = await client.SendAsync(topUpReq);
        Assert.Equal(HttpStatusCode.Created, topUpRes.StatusCode);
        var topUpData = await topUpRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        var topUpNumber = topUpData!.Data!.PaymentNumber;

        // 2. Create an Order and pay with wallet
        var sku = $"SKU-SEP-{Guid.NewGuid():N}"[..18];
        await CreateTestProductAsync(sku, 1500m);
        var orderNumber = await CreateCustomerOrderAsync(sku, authCookie, csrfToken, antiCookie, client);

        await CreditCustomerWalletAsync(customerId, 2000m);

        var payReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/orders/{orderNumber}/pay-with-wallet");
        payReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
        payReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
        var payRes = await client.SendAsync(payReq);
        Assert.Equal(HttpStatusCode.OK, payRes.StatusCode);
        var payData = await payRes.Content.ReadFromJsonAsync<ApiResponse<CustomerOrderPaymentDto>>();
        var orderPaymentNumber = payData!.Data!.PaymentNumber;

        // 3. Verify in database:
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var topUpPayment = await db.Payments.FirstAsync(p => p.PaymentNumber == topUpNumber);
            Assert.Equal(PaymentPurpose.WalletTopUp, topUpPayment.Purpose);
            Assert.NotNull(topUpPayment.WalletId);
            Assert.Null(topUpPayment.OrderId); // CK_Payments_Purpose_Target holds

            var orderPayment = await db.Payments.FirstAsync(p => p.PaymentNumber == orderPaymentNumber);
            Assert.Equal(PaymentPurpose.OrderPurchase, orderPayment.Purpose);
            Assert.NotNull(orderPayment.OrderId);
            Assert.Null(orderPayment.WalletId); // CK_Payments_Purpose_Target holds
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 13. Injected Failure Rollback Verification
    // ─────────────────────────────────────────────────────────────────────────────

    private class FaultyPaymentNumberGenerator : IPaymentNumberGenerator
    {
        public string GeneratePaymentNumber(DateTime? timestamp = null)
        {
            throw new InvalidOperationException("Simulated crash immediately after wallet debit staged!");
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_InjectedFailure_AfterDebitBeforeCommit_RollsBackAllFinancialState()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_fail_inj_{suffix}@test.local",
            Email = $"pg_fail_inj_{suffix}@test.local",
            FullName = "PG Injected Failure User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 5000.0000m // 5,000 PKR initial
        };
        seedDb.Wallets.Add(wallet);

        var orderNumber = $"ORD-INJ-FAIL-{suffix}";
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1500m,
            TotalAmount = 1500m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(order);
        await seedDb.SaveChangesAsync();

        try
        {
            await using var db = new AppDbContext(options);
            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });
            var walletService = new WalletService(db, NullLogger<WalletService>.Instance);
            var paymentService = new WalletOrderPaymentService(
                db,
                walletService,
                new FaultyPaymentNumberGenerator(), // Injected crash after debit!
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                paymentService.PayOrderWithWalletAsync(customer.Id, orderNumber, "IDEM-INJ-KEY"));

            Assert.Contains("Simulated crash immediately after wallet debit staged", ex.Message);

            // Verify PostgreSQL state: Complete rollback!
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            // Balance must be 100% restored to 5,000 PKR
            Assert.Equal(5000.0000m, verifiedWallet.Balance);
            Assert.Empty(verifiedWallet.Transactions); // 0 ledger debits

            var verifiedOrder = await verifyDb.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == order.Id);

            // Order must STILL be PendingPayment
            Assert.Equal(OrderStatus.PendingPayment, verifiedOrder.Status);
            Assert.Empty(verifiedOrder.StatusHistory); // 0 status changes

            var payments = await verifyDb.Payments.Where(p => p.OrderId == order.Id).ToListAsync();
            Assert.Empty(payments); // 0 Payment records
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var ord = await cleanDb.Orders.Include(o => o.StatusHistory).FirstOrDefaultAsync(o => o.Id == order.Id);
            if (ord != null)
            {
                cleanDb.OrderStatusHistories.RemoveRange(ord.StatusHistory);
                cleanDb.Orders.Remove(ord);
            }

            var wal = await cleanDb.Wallets.Include(w => w.Transactions).FirstOrDefaultAsync(w => w.Id == wallet.Id);
            if (wal != null)
            {
                cleanDb.WalletTransactions.RemoveRange(wal.Transactions);
                cleanDb.Wallets.Remove(wal);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);

            await cleanDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 14. Same Order with Different Idempotency Keys Never Double Debits
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentDuplicatePayment_SameOrder_DifferentIdempotencyKeys_NeverDoubleDebits()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_diff_key_{suffix}@test.local",
            Email = $"pg_diff_key_{suffix}@test.local",
            FullName = "PG Different Key User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 5000.0000m
        };
        seedDb.Wallets.Add(wallet);

        var orderNumber = $"ORD-CONC-DIFFKEY-{suffix}";
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1500m,
            TotalAmount = 1500m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.Add(order);
        await seedDb.SaveChangesAsync();

        try
        {
            await using var db1 = new AppDbContext(options);
            await using var db2 = new AppDbContext(options);

            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });

            var walletService1 = new WalletService(db1, NullLogger<WalletService>.Instance);
            var paymentService1 = new WalletOrderPaymentService(
                db1,
                walletService1,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            var walletService2 = new WalletService(db2, NullLogger<WalletService>.Instance);
            var paymentService2 = new WalletOrderPaymentService(
                db2,
                walletService2,
                new PaymentNumberGenerator(),
                purchaseOptions,
                NullLogger<WalletOrderPaymentService>.Instance);

            // Execute concurrent payments with DIFFERENT idempotency keys
            var task1 = Task.Run(() => paymentService1.PayOrderWithWalletAsync(customer.Id, orderNumber, "KEY-DIFF-A"));
            var task2 = Task.Run(() => paymentService2.PayOrderWithWalletAsync(customer.Id, orderNumber, "KEY-DIFF-B"));

            var results = await Task.WhenAll(task1, task2);

            // Exactly ONE must succeed, exactly ONE must be Conflict
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Success);
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Conflict);

            // Verify final state in PostgreSQL:
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            // Balance must be 3,500 PKR (5,000 - 1,500) — NO double debit!
            Assert.Equal(3500.0000m, verifiedWallet.Balance);
            Assert.Single(verifiedWallet.Transactions);
            Assert.Equal(1500m, verifiedWallet.Transactions.First().Amount);
            Assert.Equal(WalletTransactionType.Debit, verifiedWallet.Transactions.First().TransactionType);

            var verifiedOrder = await verifyDb.Orders.FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderStatus.Paid, verifiedOrder.Status);

            var payments = await verifyDb.Payments.Where(p => p.OrderId == order.Id).ToListAsync();
            Assert.Single(payments);
            Assert.Equal(PaymentStatus.Paid, payments.First().Status);
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId == order.Id).ToListAsync();
            foreach (var p in pmts) cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            cleanDb.Payments.RemoveRange(pmts);

            var ord = await cleanDb.Orders.Include(o => o.StatusHistory).FirstOrDefaultAsync(o => o.Id == order.Id);
            if (ord != null)
            {
                cleanDb.OrderStatusHistories.RemoveRange(ord.StatusHistory);
                cleanDb.Orders.Remove(ord);
            }

            var wal = await cleanDb.Wallets.Include(w => w.Transactions).FirstOrDefaultAsync(w => w.Id == wallet.Id);
            if (wal != null)
            {
                cleanDb.WalletTransactions.RemoveRange(wal.Transactions);
                cleanDb.Wallets.Remove(wal);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);

            await cleanDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 15. Cross-Order Idempotency Key Reuse: Same Customer vs Different Customer
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_SameIdempotencyKey_AcrossDifferentOrders_SameCustomerBlocked_DifferentCustomersAllowed()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer1 = new ApplicationUser
        {
            UserName = $"pg_cross_k1_{suffix}@test.local",
            Email = $"pg_cross_k1_{suffix}@test.local",
            FullName = "PG Cross Key User 1",
            EmailConfirmed = true,
            IsActive = true
        };
        var customer2 = new ApplicationUser
        {
            UserName = $"pg_cross_k2_{suffix}@test.local",
            Email = $"pg_cross_k2_{suffix}@test.local",
            FullName = "PG Cross Key User 2",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.AddRange(customer1, customer2);

        var wallet1 = new Wallet { CustomerId = customer1.Id, Currency = "PKR", Balance = 5000m };
        var wallet2 = new Wallet { CustomerId = customer2.Id, Currency = "PKR", Balance = 5000m };
        seedDb.Wallets.AddRange(wallet1, wallet2);

        var order1Cust1 = new Order
        {
            OrderNumber = $"ORD-C1-O1-{suffix}",
            CustomerId = customer1.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1000m,
            TotalAmount = 1000m,
            SalesChannel = SalesChannel.Website
        };
        var order2Cust1 = new Order
        {
            OrderNumber = $"ORD-C1-O2-{suffix}",
            CustomerId = customer1.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1000m,
            TotalAmount = 1000m,
            SalesChannel = SalesChannel.Website
        };
        var order1Cust2 = new Order
        {
            OrderNumber = $"ORD-C2-O1-{suffix}",
            CustomerId = customer2.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1000m,
            TotalAmount = 1000m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.AddRange(order1Cust1, order2Cust1, order1Cust2);
        await seedDb.SaveChangesAsync();

        var sharedKey = $"SHARED-REUSE-KEY-{suffix}";

        try
        {
            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });

            // 1. Customer 1 pays Order 1 with sharedKey -> Success
            await using (var db = new AppDbContext(options))
            {
                var walletService = new WalletService(db, NullLogger<WalletService>.Instance);
                var paymentService = new WalletOrderPaymentService(db, walletService, new PaymentNumberGenerator(), purchaseOptions, NullLogger<WalletOrderPaymentService>.Instance);

                var res1 = await paymentService.PayOrderWithWalletAsync(customer1.Id, order1Cust1.OrderNumber, sharedKey);
                Assert.Equal(WalletOrderPaymentStatus.Success, res1.Status);
            }

            // 2. Customer 1 tries to reuse sharedKey on Order 2 -> 409 Conflict
            await using (var db = new AppDbContext(options))
            {
                var walletService = new WalletService(db, NullLogger<WalletService>.Instance);
                var paymentService = new WalletOrderPaymentService(db, walletService, new PaymentNumberGenerator(), purchaseOptions, NullLogger<WalletOrderPaymentService>.Instance);

                var res2 = await paymentService.PayOrderWithWalletAsync(customer1.Id, order2Cust1.OrderNumber, sharedKey);
                Assert.Equal(WalletOrderPaymentStatus.Conflict, res2.Status);
                Assert.Contains("already been used for a different order payment", res2.Message);
            }

            // 3. Customer 2 uses the SAME sharedKey for their own order (Order 1 of Customer 2) -> Success!
            // Confirms different customers can independently use the same key without interference.
            await using (var db = new AppDbContext(options))
            {
                var walletService = new WalletService(db, NullLogger<WalletService>.Instance);
                var paymentService = new WalletOrderPaymentService(db, walletService, new PaymentNumberGenerator(), purchaseOptions, NullLogger<WalletOrderPaymentService>.Instance);

                var res3 = await paymentService.PayOrderWithWalletAsync(customer2.Id, order1Cust2.OrderNumber, sharedKey);
                Assert.Equal(WalletOrderPaymentStatus.Success, res3.Status);
            }

            // Verify database states
            await using (var verifyDb = new AppDbContext(options))
            {
                var o1c1 = await verifyDb.Orders.FirstAsync(o => o.Id == order1Cust1.Id);
                var o2c1 = await verifyDb.Orders.FirstAsync(o => o.Id == order2Cust1.Id);
                var o1c2 = await verifyDb.Orders.FirstAsync(o => o.Id == order1Cust2.Id);

                Assert.Equal(OrderStatus.Paid, o1c1.Status);
                Assert.Equal(OrderStatus.PendingPayment, o2c1.Status); // Unpaid due to conflict
                Assert.Equal(OrderStatus.Paid, o1c2.Status); // Paid for Customer 2

                var w1 = await verifyDb.Wallets.FirstAsync(w => w.Id == wallet1.Id);
                Assert.Equal(4000m, w1.Balance); // Debited once only
                var w2 = await verifyDb.Wallets.FirstAsync(w => w.Id == wallet2.Id);
                Assert.Equal(4000m, w2.Balance); // Debited once only
            }
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var orderIds = new[] { order1Cust1.Id, order2Cust1.Id, order1Cust2.Id };
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId != null && orderIds.Contains(p.OrderId.Value)).ToListAsync();
            foreach (var p in pmts) cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            cleanDb.Payments.RemoveRange(pmts);

            var orders = await cleanDb.Orders.Include(o => o.StatusHistory).Where(o => orderIds.Contains(o.Id)).ToListAsync();
            foreach (var o in orders) cleanDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            cleanDb.Orders.RemoveRange(orders);

            var wals = await cleanDb.Wallets.Include(w => w.Transactions).Where(w => w.Id == wallet1.Id || w.Id == wallet2.Id).ToListAsync();
            foreach (var w in wals) cleanDb.WalletTransactions.RemoveRange(w.Transactions);
            cleanDb.Wallets.RemoveRange(wals);

            var usrs = await cleanDb.Users.Where(u => u.Id == customer1.Id || u.Id == customer2.Id).ToListAsync();
            cleanDb.Users.RemoveRange(usrs);

            await cleanDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 16. Concurrent Same-Customer Orders Racing with the Same Idempotency Key
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentCompetingOrders_SameCustomer_SameIdempotencyKey_ExactlyOneSucceeds()
    {
        var options = PostgreSqlTestDatabase.CreateOptions();
        await using var seedDb = new AppDbContext(options);

        if (!await PostgreSqlTestDatabase.EnsureMigratedAsync(seedDb))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new ApplicationUser
        {
            UserName = $"pg_conc_samekey_{suffix}@test.local",
            Email = $"pg_conc_samekey_{suffix}@test.local",
            FullName = "PG Conc Same Key User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        var wallet = new Wallet { CustomerId = customer.Id, Currency = "PKR", Balance = 5000m };
        seedDb.Wallets.Add(wallet);

        var orderA = new Order
        {
            OrderNumber = $"ORD-CONC-KEY-A-{suffix}",
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1000m,
            TotalAmount = 1000m,
            SalesChannel = SalesChannel.Website
        };
        var orderB = new Order
        {
            OrderNumber = $"ORD-CONC-KEY-B-{suffix}",
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 1000m,
            TotalAmount = 1000m,
            SalesChannel = SalesChannel.Website
        };
        seedDb.Orders.AddRange(orderA, orderB);
        await seedDb.SaveChangesAsync();

        var sharedKey = $"CONC-RACE-KEY-{suffix}";

        try
        {
            await using var dbA = new AppDbContext(options);
            await using var dbB = new AppDbContext(options);

            var purchaseOptions = Options.Create(new WalletPurchaseOptions { Enabled = true, Currency = "PKR" });

            var walletServiceA = new WalletService(dbA, NullLogger<WalletService>.Instance);
            var paymentServiceA = new WalletOrderPaymentService(dbA, walletServiceA, new PaymentNumberGenerator(), purchaseOptions, NullLogger<WalletOrderPaymentService>.Instance);

            var walletServiceB = new WalletService(dbB, NullLogger<WalletService>.Instance);
            var paymentServiceB = new WalletOrderPaymentService(dbB, walletServiceB, new PaymentNumberGenerator(), purchaseOptions, NullLogger<WalletOrderPaymentService>.Instance);

            // Execute concurrent payments with the SAME idempotency key across DIFFERENT orders
            var taskA = Task.Run(() => paymentServiceA.PayOrderWithWalletAsync(customer.Id, orderA.OrderNumber, sharedKey));
            var taskB = Task.Run(() => paymentServiceB.PayOrderWithWalletAsync(customer.Id, orderB.OrderNumber, sharedKey));

            var results = await Task.WhenAll(taskA, taskB);

            // Exactly ONE must succeed, exactly ONE must be Conflict
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Success);
            Assert.Contains(results, r => r.Status == WalletOrderPaymentStatus.Conflict);

            // Verify final state in PostgreSQL:
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            // Only ONE order debited (Balance: 4,000 PKR, exactly 1 debit transaction)
            Assert.Equal(4000.0000m, verifiedWallet.Balance);
            Assert.Single(verifiedWallet.Transactions);

            var ordA = await verifyDb.Orders.FirstAsync(o => o.Id == orderA.Id);
            var ordB = await verifyDb.Orders.FirstAsync(o => o.Id == orderB.Id);

            var paidCount = (ordA.Status == OrderStatus.Paid ? 1 : 0) + (ordB.Status == OrderStatus.Paid ? 1 : 0);
            var pendingCount = (ordA.Status == OrderStatus.PendingPayment ? 1 : 0) + (ordB.Status == OrderStatus.PendingPayment ? 1 : 0);
            Assert.Equal(1, paidCount);
            Assert.Equal(1, pendingCount);
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var orderIds = new[] { orderA.Id, orderB.Id };
            var pmts = await cleanDb.Payments.Include(p => p.StatusHistory).Where(p => p.OrderId != null && orderIds.Contains(p.OrderId.Value)).ToListAsync();
            foreach (var p in pmts) cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            cleanDb.Payments.RemoveRange(pmts);

            var orders = await cleanDb.Orders.Include(o => o.StatusHistory).Where(o => orderIds.Contains(o.Id)).ToListAsync();
            foreach (var o in orders) cleanDb.OrderStatusHistories.RemoveRange(o.StatusHistory);
            cleanDb.Orders.RemoveRange(orders);

            var wal = await cleanDb.Wallets.Include(w => w.Transactions).FirstOrDefaultAsync(w => w.Id == wallet.Id);
            if (wal != null)
            {
                cleanDb.WalletTransactions.RemoveRange(wal.Transactions);
                cleanDb.Wallets.Remove(wal);
            }

            var usr = await cleanDb.Users.FindAsync(customer.Id);
            if (usr != null) cleanDb.Users.Remove(usr);

            await cleanDb.SaveChangesAsync();
        }
    }
}
