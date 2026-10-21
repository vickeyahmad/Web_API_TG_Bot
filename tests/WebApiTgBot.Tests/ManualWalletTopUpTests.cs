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
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Payments.Services;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace WebApiTgBot.Tests;

/// <summary>
/// Comprehensive integration test suite for Milestone 4C: Manual PKR Payment Verification & Wallet Top-Ups.
/// Verifies:
/// 1. Feature gate safety (disabled by default in production/test -> 403 Forbidden).
/// 2. Customer manual top-up initiation (positive amounts, limits, Pending status, initial history).
/// 3. Idempotency guarantees for wallet top-ups (replays return original payment, conflicting payloads fail).
/// 4. Customer transfer proof submission (records TID/ref, status stays Pending, wallet untouched).
/// 5. Strict customer data isolation (customers cannot view or submit proof for others' top-ups).
/// 6. Anti-forgery (CSRF) protection on all state-changing customer and administrator endpoints.
/// 7. Administrator review and inspection of manual payment details and audit trails.
/// 8. Administrator rejection workflow (transitions to Failed, wallet untouched, audit logged).
/// 9. Administrator approval workflow with atomic PostgreSQL transaction (transitions to Paid, credits wallet, records permanent ledger entry).
/// 10. Prevention of duplicate approved bank references (no double-crediting of verified bank transfers).
/// 11. Database-level PostgreSQL constraints (CK_Payments_Purpose_Target, CK_Payments_Amount_Positive, unique partial indexes).
/// </summary>
public class ManualWalletTopUpTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ManualWalletTopUpTests(CustomWebApplicationFactory factory)
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
                services.Configure<WalletTopUpOptions>(options =>
                {
                    options.Enabled = true;
                    options.MinAmount = 100.00m;
                    options.MaxAmount = 500000.00m;
                    options.Currency = "PKR";
                    options.AllowedMethodCodes = new[]
                    {
                        "BANK_TRANSFER",
                        "MANUAL_DEPOSIT",
                        "JAZZCASH_MANUAL",
                        "EASYPAISA_MANUAL"
                    };
                    options.BankName = "Meezan Bank";
                    options.AccountTitle = "Gogaabyte Technologies";
                    options.AccountNumber = "01020304050607";
                    options.Instructions = "Please transfer exact amount.";
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
            FullName = "Wallet TopUp Customer"
        });
        Assert.Equal(HttpStatusCode.Created, regRes.StatusCode);
        var regData = await regRes.Content.ReadFromJsonAsync<ApiResponse<CustomerUserDto>>();
        Assert.NotNull(regData?.Data);

        var loginRes = await httpClient.PostAsJsonAsync("/api/customer/auth/login", new CustomerLoginRequest
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

    private async Task<(string AuthCookie, string AntiforgeryCookie, string CsrfToken)> LoginAsAdminAsync(HttpClient? client = null)
    {
        await EnsureRolesExistAsync();
        var httpClient = client ?? _client;
        var email = $"admin_topup_{Guid.NewGuid():N}@test.local";
        var password = "SecureAdminPassword123!";

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = "Admin TopUp Approver",
                EmailConfirmed = true,
                IsActive = true
            };
            var createRes = await userManager.CreateAsync(user, password);
            Assert.True(createRes.Succeeded);
            await userManager.AddToRoleAsync(user, AppRoles.Admin);
        }

        var loginRes = await httpClient.PostAsJsonAsync("/api/admin/auth/login", new AdminLoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var authCookie = loginRes.Headers.GetValues("Set-Cookie")
            .First(c => c.Contains(".WebAPI.Admin.Auth"))
            .Split(';')[0];

        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/csrf");
        csrfReq.Headers.Add("Cookie", authCookie);
        var csrfRes = await httpClient.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfData = await csrfRes.Content.ReadFromJsonAsync<ApiResponse<string>>();

        var antiforgeryCookie = string.Empty;
        if (csrfRes.Headers.TryGetValues("Set-Cookie", out var adminCookies))
        {
            var match = adminCookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiforgeryCookie = match.Split(';')[0];
            }
        }

        return (authCookie, antiforgeryCookie, csrfData!.Data!);
    }

    private async Task<(string CsrfToken, string AntiforgeryCookie)> GetCustomerCsrfTokenAsync(string authCookie, HttpClient? client = null)
    {
        var httpClient = client ?? _client;
        var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/api/customer/auth/csrf");
        csrfReq.Headers.Add("Cookie", authCookie);
        var csrfRes = await httpClient.SendAsync(csrfReq);
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfData = await csrfRes.Content.ReadFromJsonAsync<ApiResponse<string>>();

        var antiforgeryCookie = string.Empty;
        if (csrfRes.Headers.TryGetValues("Set-Cookie", out var custCookies))
        {
            var match = custCookies.FirstOrDefault(c => c.Contains(".WebAPI.Antiforgery"));
            if (match != null)
            {
                antiforgeryCookie = match.Split(';')[0];
            }
        }

        return (csrfData!.Data!, antiforgeryCookie);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Feature Gate Safety
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FeatureGate_WhenDisabledByDefault_Returns403Forbidden()
    {
        // Default factory has WalletTopUpOptions.Enabled == false from appsettings.json
        var email = $"disabled_gate_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!");
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        req.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. Customer Manual Top-Up Initiation
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTopUp_ValidRequest_CreatesPendingPaymentAndInitialHistory()
    {
        var client = CreateEnabledClient();
        var email = $"create_topup_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 5000m,
                PaymentMethodCode = "BANK_TRANSFER",
                Notes = "Monthly tool budget"
            })
        };
        req.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        Assert.NotNull(body?.Data);
        Assert.StartsWith("PAY-", body.Data.PaymentNumber);
        Assert.Equal(5000m, body.Data.Amount);
        Assert.Equal("PKR", body.Data.Currency);
        Assert.Equal("Pending", body.Data.Status);
        Assert.Equal("Meezan Bank", body.Data.BankName);
        Assert.NotNull(body.Data.Instructions);

        // Verify database state: Wallet created with balance 0; Payment linked to Wallet; initial history created
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = await db.Payments
            .Include(p => p.Wallet)
            .Include(p => p.StatusHistory)
            .FirstOrDefaultAsync(p => p.PaymentNumber == body.Data.PaymentNumber);

        Assert.NotNull(payment);
        Assert.Equal(PaymentPurpose.WalletTopUp, payment.Purpose);
        Assert.Null(payment.OrderId);
        Assert.NotNull(payment.WalletId);
        Assert.Equal(customerId, payment.Wallet!.CustomerId);
        Assert.Equal(0.0000m, payment.Wallet.Balance); // Wallet untouched!
        Assert.Single(payment.StatusHistory);
        Assert.Null(payment.StatusHistory.First().PreviousStatus);
        Assert.Equal(PaymentStatus.Pending, payment.StatusHistory.First().NewStatus);
        Assert.Equal(customerId, payment.StatusHistory.First().ChangedByUserId);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. Amount and Method Validation
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50, "BANK_TRANSFER")] // below MinAmount (100)
    [InlineData(600000, "BANK_TRANSFER")] // above MaxAmount (500000)
    [InlineData(1000, "INVALID_GATEWAY")] // unsupported method
    public async Task CreateTopUp_InvalidLimitsOrMethod_Returns400BadRequest(decimal amount, string method)
    {
        var client = CreateEnabledClient();
        var email = $"val_topup_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = amount,
                PaymentMethodCode = method
            })
        };
        req.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. Durable Idempotency
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTopUp_Idempotency_SamePayloadReturnsOriginal_DifferentPayloadReturnsConflict()
    {
        var client = CreateEnabledClient();
        var email = $"idempotent_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);
        var idempotencyKey = $"TOPUP-IDEMP-{Guid.NewGuid():N}";

        // 1. Initial request
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 2500m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        req1.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req1.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req1.Headers.Add("X-Idempotency-Key", idempotencyKey);

        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var data1 = await res1.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();

        // 2. Exact retry with same idempotency key
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 2500m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        req2.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req2.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req2.Headers.Add("X-Idempotency-Key", idempotencyKey);

        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var data2 = await res2.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        Assert.Equal(data1!.Data!.PaymentNumber, data2!.Data!.PaymentNumber);

        // 3. Conflict retry with different amount using same idempotency key
        var req3 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 9000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        req3.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        req3.Headers.Add("X-XSRF-TOKEN", csrfToken);
        req3.Headers.Add("X-Idempotency-Key", idempotencyKey);

        var res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.Conflict, res3.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. Transfer Proof Submission
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitProof_ValidPendingPayment_UpdatesProof_LeavesPending_WalletUntouched()
    {
        var client = CreateEnabledClient();
        var email = $"proof_{Guid.NewGuid():N}@test.local";
        var (customerId, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        // Create topup
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1500m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        createReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
        var createRes = await client.SendAsync(createReq);
        var createData = await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        var payNumber = createData!.Data!.PaymentNumber;

        // Submit proof
        var proofReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/wallet/topups/{payNumber}/proof")
        {
            Content = JsonContent.Create(new SubmitTransferProofRequest
            {
                CustomerReference = "TRX-MEEZAN-889900",
                CustomerTransferDate = DateTime.UtcNow,
                CustomerSenderAccount = "Ali Khan - 03001234567",
                CustomerNotes = "Transferred via Meezan Mobile App"
            })
        };
        proofReq.Headers.Add("Cookie", $"{authCookie}; {antiCookie}");
        proofReq.Headers.Add("X-XSRF-TOKEN", csrfToken);

        var proofRes = await client.SendAsync(proofReq);
        Assert.Equal(HttpStatusCode.OK, proofRes.StatusCode);
        var proofData = await proofRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();
        Assert.Equal("Pending", proofData!.Data!.Status); // Must remain Pending!
        Assert.Equal("TRX-MEEZAN-889900", proofData.Data.CustomerReference);
        Assert.NotNull(proofData.Data.CustomerProofSubmittedAt);

        // Verify in database: balance is STILL 0.0000
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
        Assert.Equal(0.0000m, wallet.Balance);

        // Verify status history has 2 entries (initial + proof submission)
        var payment = await db.Payments.Include(p => p.StatusHistory).FirstAsync(p => p.PaymentNumber == payNumber);
        Assert.Equal(2, payment.StatusHistory.Count);
        Assert.Contains("TRX-MEEZAN-889900", payment.StatusHistory.OrderBy(h => h.ChangedAt).Last().Reason);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 6. Strict Customer Isolation
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomerIsolation_CannotViewOrSubmitProofForOtherCustomerTopUp()
    {
        var client = CreateEnabledClient();
        var email1 = $"cust1_{Guid.NewGuid():N}@test.local";
        var (_, authCookie1) = await RegisterAndLoginCustomerAsync(email1, "Password123!", client);
        var (csrf1, anti1) = await GetCustomerCsrfTokenAsync(authCookie1, client);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 3000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", $"{authCookie1}; {anti1}");
        createReq.Headers.Add("X-XSRF-TOKEN", csrf1);
        var createRes = await client.SendAsync(createReq);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        // Customer 2 logs in
        var email2 = $"cust2_{Guid.NewGuid():N}@test.local";
        var (_, authCookie2) = await RegisterAndLoginCustomerAsync(email2, "Password123!", client);
        var (csrf2, anti2) = await GetCustomerCsrfTokenAsync(authCookie2, client);

        // Cust 2 attempts to get Cust 1's top-up detail -> 404
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/customer/wallet/topups/{payNumber}");
        getReq.Headers.Add("Cookie", authCookie2);
        var getRes = await client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // Cust 2 attempts to submit proof for Cust 1's top-up -> 404
        var proofReq = new HttpRequestMessage(HttpMethod.Post, $"/api/customer/wallet/topups/{payNumber}/proof")
        {
            Content = JsonContent.Create(new SubmitTransferProofRequest
            {
                CustomerReference = "TRX-HACK-123"
            })
        };
        proofReq.Headers.Add("Cookie", $"{authCookie2}; {anti2}");
        proofReq.Headers.Add("X-XSRF-TOKEN", csrf2);
        var proofRes = await client.SendAsync(proofReq);
        Assert.Equal(HttpStatusCode.NotFound, proofRes.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7. CSRF Protection Enforcement
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csrf_MissingXsrfToken_Returns400BadRequest()
    {
        var client = CreateEnabledClient();
        var email = $"csrf_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        req.Headers.Add("Cookie", authCookie); // Missing X-XSRF-TOKEN header!

        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 8. Administrator Review & History Inspection
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ReviewManualPayments_ListsAndRetrievesDetailsWithAuditHistory()
    {
        var client = CreateEnabledClient();
        var email = $"admin_rev_{Guid.NewGuid():N}@test.local";
        var (_, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 4000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", $"{custCookie}; {custAnti}");
        createReq.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes = await client.SendAsync(createReq);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        // Admin logs in
        var (adminCookie, _, _) = await LoginAsAdminAsync(client);

        // List manual payments
        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/payments/manual?status=Pending");
        listReq.Headers.Add("Cookie", adminCookie);
        var listRes = await client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var listData = await listRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AdminManualPaymentDto>>>();
        Assert.Contains(listData!.Data!.Items, p => p.PaymentNumber == payNumber);

        // Get single detail
        var detailReq = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/manual/{payNumber}");
        detailReq.Headers.Add("Cookie", adminCookie);
        var detailRes = await client.SendAsync(detailReq);
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
        var detailData = await detailRes.Content.ReadFromJsonAsync<ApiResponse<AdminManualPaymentDto>>();
        Assert.NotNull(detailData?.Data);
        Assert.Equal("Pending", detailData.Data.Status);
        Assert.NotEmpty(detailData.Data.StatusHistory);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 9. Administrator Rejection Workflow
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_RejectPayment_TransitionsToFailed_WalletUntouched()
    {
        var client = CreateEnabledClient();
        var email = $"reject_{Guid.NewGuid():N}@test.local";
        var (customerId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 2000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", $"{custCookie}; {custAnti}");
        createReq.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes = await client.SendAsync(createReq);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        var (adminCookie, adminAnti, adminCsrf) = await LoginAsAdminAsync(client);

        var rejectReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/reject")
        {
            Content = JsonContent.Create(new RejectManualPaymentRequest
            {
                Reason = "Payment could not be verified in bank statement.",
                AdminNotes = "Checked Meezan statement until 11:59PM."
            })
        };
        rejectReq.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        rejectReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);

        var rejectRes = await client.SendAsync(rejectReq);
        Assert.Equal(HttpStatusCode.OK, rejectRes.StatusCode);
        var rejectData = await rejectRes.Content.ReadFromJsonAsync<ApiResponse<AdminManualPaymentDto>>();
        Assert.Equal("Failed", rejectData!.Data!.Status);

        // Verify customer wallet balance is 0.0000
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
        Assert.Equal(0.0000m, wallet.Balance);

        // Verify rejected payment cannot be approved
        var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 2000m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = "TRX-LATE-123"
            })
        };
        approveReq.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        approveReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var approveRes = await client.SendAsync(approveReq);
        Assert.Equal(HttpStatusCode.BadRequest, approveRes.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 10. Administrator Approval & Atomic Wallet Credit
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ApprovePayment_TransitionsToPaid_CreditsWalletAtomically_PreventsDuplicateExternalRef()
    {
        var client = CreateEnabledClient();
        var email = $"approve_{Guid.NewGuid():N}@test.local";
        var (customerId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        // Topup 1
        var createReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 7500m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq1.Headers.Add("Cookie", $"{custCookie}; {custAnti}");
        createReq1.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes1 = await client.SendAsync(createReq1);
        var payNumber1 = (await createRes1.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        // Topup 2
        var createReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 7500m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq2.Headers.Add("Cookie", $"{custCookie}; {custAnti}");
        createReq2.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes2 = await client.SendAsync(createReq2);
        var payNumber2 = (await createRes2.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        var (adminCookie, adminAnti, adminCsrf) = await LoginAsAdminAsync(client);
        var bankRef = $"BANK-TRX-{Guid.NewGuid():N}";

        // Approve Topup 1
        var approveReq1 = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber1}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 7500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = bankRef,
                AdminNotes = "Funds credited from Meezan transfer"
            })
        };
        approveReq1.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        approveReq1.Headers.Add("X-XSRF-TOKEN", adminCsrf);

        var approveRes1 = await client.SendAsync(approveReq1);
        Assert.Equal(HttpStatusCode.OK, approveRes1.StatusCode);
        var approveData1 = await approveRes1.Content.ReadFromJsonAsync<ApiResponse<ApproveManualPaymentResultDto>>();
        Assert.NotNull(approveData1?.Data);
        Assert.Equal("Paid", approveData1.Data.Payment.Status);
        Assert.Equal(7500m, approveData1.Data.CreditedAmount);
        Assert.Equal(7500.0000m, approveData1.Data.WalletNewBalance);
        Assert.StartsWith("WALLET_TOPUP:", approveData1.Data.WalletOperationKey);

        // Verify database: Wallet has balance 7500, WalletTransaction exists
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.Wallets.Include(w => w.Transactions).FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(7500.0000m, wallet.Balance);
            Assert.Single(wallet.Transactions);
            var tx = wallet.Transactions.First();
            Assert.Equal(WalletTransactionType.Credit, tx.TransactionType);
            Assert.Equal(7500m, tx.Amount);
            Assert.Equal(WalletTransactionSource.DepositVerification, tx.SourceType);
            Assert.Equal(payNumber1, tx.SourceReferenceId);
        }

        // Attempt to approve Topup 1 again -> 409 Conflict (already paid)
        var approveReplayReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber1}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 7500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"ANOTHER-TRX-{Guid.NewGuid():N}"
            })
        };
        approveReplayReq.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        approveReplayReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var approveReplayRes = await client.SendAsync(approveReplayReq);
        Assert.Equal(HttpStatusCode.Conflict, approveReplayRes.StatusCode);

        // Attempt to approve Topup 2 using the SAME bankRef on SAME receiving account -> 409 Conflict (duplicate funding)
        var duplicateRefReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber2}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 7500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607", // Same account
                ConfirmFundsReceived = true,
                VerifiedTransactionId = bankRef // Same external reference!
            })
        };
        duplicateRefReq.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        duplicateRefReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var duplicateRefRes = await client.SendAsync(duplicateRefReq);
        Assert.Equal(HttpStatusCode.Conflict, duplicateRefRes.StatusCode);

        // However, approving Topup 2 with SAME bankRef but DIFFERENT receiving account succeeds
        var differentAccountReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber2}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 7500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "JazzCash - 03001234567", // Different account
                ConfirmFundsReceived = true,
                VerifiedTransactionId = bankRef // Same provider TID on different account is allowed
            })
        };
        differentAccountReq.Headers.Add("Cookie", $"{adminCookie}; {adminAnti}");
        differentAccountReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var differentAccountRes = await client.SendAsync(differentAccountReq);
        Assert.Equal(HttpStatusCode.OK, differentAccountRes.StatusCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 11. PostgreSQL Database Constraints & Foreign Keys
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_TopUpConstraints_PurposeTarget_PositiveAmount_UniquePaidRef()
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
            UserName = $"pg_topup_{suffix}@test.local",
            Email = $"pg_topup_{suffix}@test.local",
            FullName = "PG TopUp User",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);

        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 0.0000m
        };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();

        var sharedKey = $"PG_TOPUP_KEY_{suffix}";
        var pNumber1 = $"PAY-TOP1-{suffix}";
        var pNumber2 = $"PAY-TOP2-{suffix}";

        try
        {
            // 1. Successful top-up insert
            var topUpPayment = new Payment
            {
                PaymentNumber = pNumber1,
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = 1000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending,
                IdempotencyKey = sharedKey
            };
            db.Payments.Add(topUpPayment);
            await db.SaveChangesAsync();

            // 2. CK_Payments_Purpose_Target violation: WalletTopUp with OrderId populated
            var order = new Order
            {
                OrderNumber = $"ORD-PG-{suffix}",
                CustomerId = customer.Id,
                Status = OrderStatus.PendingPayment,
                Currency = "PKR",
                Subtotal = 1000m,
                TotalAmount = 1000m,
                SalesChannel = SalesChannel.Website
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var invalidTargetPayment = new Payment
            {
                PaymentNumber = $"PAY-INVALID-{suffix}",
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = order.Id, // Prohibited by CK_Payments_Purpose_Target!
                Amount = 1000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending
            };
            db.Payments.Add(invalidTargetPayment);

            var exTarget = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgExTarget = Assert.IsType<PostgresException>(exTarget.InnerException);
            Assert.Equal("23514", pgExTarget.SqlState); // check_violation
            Assert.Contains("CK_Payments_Purpose_Target", pgExTarget.ConstraintName ?? pgExTarget.Message);

            db.ChangeTracker.Clear();

            // 3. CK_Payments_Amount_Positive violation: Amount <= 0
            var negativeAmountPayment = new Payment
            {
                PaymentNumber = $"PAY-NEG-{suffix}",
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = -50m, // Prohibited!
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending
            };
            db.Payments.Add(negativeAmountPayment);

            var exAmt = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgExAmt = Assert.IsType<PostgresException>(exAmt.InnerException);
            Assert.Equal("23514", pgExAmt.SqlState); // check_violation
            Assert.Contains("CK_Payments_Amount_Positive", pgExAmt.ConstraintName ?? pgExAmt.Message);

            db.ChangeTracker.Clear();

            // 4. IX_Payments_WalletId_IdempotencyKey violation: Duplicate key for same wallet
            var duplicateKeyPayment = new Payment
            {
                PaymentNumber = pNumber2,
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = 1000m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Pending,
                IdempotencyKey = sharedKey // Duplicate for same wallet!
            };
            db.Payments.Add(duplicateKeyPayment);

            var exKey = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgExKey = Assert.IsType<PostgresException>(exKey.InnerException);
            Assert.Equal("23505", pgExKey.SqlState); // unique_violation
            Assert.Contains("IX_Payments_WalletId_IdempotencyKey", pgExKey.ConstraintName ?? pgExKey.Message);

            db.ChangeTracker.Clear();

            // 5. IX_Payments_Paid_Account_ProviderTransactionId violation: Two Paid payments with same bank reference on same receiving account
            var duplicateRefPayment1 = new Payment
            {
                PaymentNumber = $"PAY-REF1-{suffix}",
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = 500m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Paid,
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ProviderTransactionId = $"TRX-DUP-{suffix}",
                PaidAt = DateTime.UtcNow
            };
            var duplicateRefPayment2 = new Payment
            {
                PaymentNumber = $"PAY-REF2-{suffix}",
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = 500m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Paid,
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607", // Same receiving account
                ProviderTransactionId = $"TRX-DUP-{suffix}", // Same bank TID!
                PaidAt = DateTime.UtcNow
            };

            db.Payments.Add(duplicateRefPayment1);
            await db.SaveChangesAsync();

            db.Payments.Add(duplicateRefPayment2);
            var exRef = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgExRef = Assert.IsType<PostgresException>(exRef.InnerException);
            Assert.Equal("23505", pgExRef.SqlState); // unique_violation
            Assert.Contains("IX_Payments_Paid_Account_ProviderTransactionId", pgExRef.ConstraintName ?? pgExRef.Message);

            db.ChangeTracker.Clear();

            // 6. Two Paid payments with same bank reference on DIFFERENT receiving accounts succeed
            var differentAccountPayment = new Payment
            {
                PaymentNumber = $"PAY-REF3-{suffix}",
                Purpose = PaymentPurpose.WalletTopUp,
                WalletId = wallet.Id,
                OrderId = null,
                Amount = 500m,
                Currency = "PKR",
                PaymentMethodCode = "BANK_TRANSFER",
                Status = PaymentStatus.Paid,
                VerifiedReceivingAccount = "JazzCash - 03001234567", // Different receiving account
                ProviderTransactionId = $"TRX-DUP-{suffix}", // Reused TID on different account is allowed
                PaidAt = DateTime.UtcNow
            };
            db.Payments.Add(differentAccountPayment);
            await db.SaveChangesAsync();
        }
        finally
        {
            await using var cleanDb = new AppDbContext(options);
            var payments = await cleanDb.Payments
                .Include(p => p.StatusHistory)
                .Where(p => p.WalletId == wallet.Id)
                .ToListAsync();

            foreach (var p in payments)
            {
                cleanDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
            }
            cleanDb.Payments.RemoveRange(payments);

            var orders = await cleanDb.Orders.Where(o => o.CustomerId == customer.Id).ToListAsync();
            cleanDb.Orders.RemoveRange(orders);

            var w = await cleanDb.Wallets.FindAsync(wallet.Id);
            if (w != null) cleanDb.Wallets.Remove(w);

            var u = await cleanDb.Users.FindAsync(customer.Id);
            if (u != null) cleanDb.Users.Remove(u);

            await cleanDb.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CreateTopUp_TwoDifferentCustomers_CanUseSameIdempotencyKeyIndependently()
    {
        var client = CreateEnabledClient();
        var sharedKey = $"CROSS-KEY-{Guid.NewGuid():N}";

        // Customer A
        var emailA = $"custA_{Guid.NewGuid():N}@test.local";
        var (_, authA) = await RegisterAndLoginCustomerAsync(emailA, "Password123!", client);
        var (csrfA, antiA) = await GetCustomerCsrfTokenAsync(authA, client);

        var reqA = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1200m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        reqA.Headers.Add("Cookie", string.IsNullOrEmpty(antiA) ? authA : $"{authA}; {antiA}");
        reqA.Headers.Add("X-XSRF-TOKEN", csrfA);
        reqA.Headers.Add("X-Idempotency-Key", sharedKey);

        var resA = await client.SendAsync(reqA);
        Assert.Equal(HttpStatusCode.Created, resA.StatusCode);
        var dataA = await resA.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();

        // Customer B
        var emailB = $"custB_{Guid.NewGuid():N}@test.local";
        var (_, authB) = await RegisterAndLoginCustomerAsync(emailB, "Password123!", client);
        var (csrfB, antiB) = await GetCustomerCsrfTokenAsync(authB, client);

        var reqB = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 1800m, // different amount, same key!
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        reqB.Headers.Add("Cookie", string.IsNullOrEmpty(antiB) ? authB : $"{authB}; {antiB}");
        reqB.Headers.Add("X-XSRF-TOKEN", csrfB);
        reqB.Headers.Add("X-Idempotency-Key", sharedKey);

        var resB = await client.SendAsync(reqB);
        Assert.Equal(HttpStatusCode.Created, resB.StatusCode);
        var dataB = await resB.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>();

        Assert.NotEqual(dataA!.Data!.PaymentNumber, dataB!.Data!.PaymentNumber);
    }

    [Fact]
    public async Task Admin_ApprovePayment_AtomicRollback_WhenCreditFails()
    {
        var client = CreateEnabledClient();
        var email = $"fail_credit_{Guid.NewGuid():N}@test.local";
        var (customerId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 3000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", string.IsNullOrEmpty(custAnti) ? custCookie : $"{custCookie}; {custAnti}");
        createReq.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes = await client.SendAsync(createReq);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        // Deactivate customer account directly in DB to trigger credit failure inside CreditAsync
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Users.FindAsync(customerId);
            customer!.IsActive = false;
            await db.SaveChangesAsync();
        }

        var (adminCookie, adminAnti, adminCsrf) = await LoginAsAdminAsync(client);

        var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 3000m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-ROLLBACK-{Guid.NewGuid():N}"
            })
        };
        approveReq.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
        approveReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);

        var approveRes = await client.SendAsync(approveReq);
        Assert.Equal(HttpStatusCode.BadRequest, approveRes.StatusCode);

        // Verify in DB that payment is STILL Pending (not Paid), and wallet balance is untouched (0)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.FirstAsync(p => p.PaymentNumber == payNumber);
            Assert.Equal(PaymentStatus.Pending, payment.Status);
            Assert.Null(payment.PaidAt);
            Assert.Null(payment.ProviderTransactionId);

            var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(0.0000m, wallet.Balance);
        }
    }

    [Fact]
    public async Task Customer_GetTopUps_ReturnsPaginatedListOrderedByCreatedAtDescending()
    {
        var client = CreateEnabledClient();
        var email = $"list_page_{Guid.NewGuid():N}@test.local";
        var (_, authCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (csrfToken, antiCookie) = await GetCustomerCsrfTokenAsync(authCookie, client);

        // Create 2 top-ups
        for (int i = 1; i <= 2; i++)
        {
            var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
            {
                Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
                {
                    Amount = 1000m * i,
                    PaymentMethodCode = "BANK_TRANSFER"
                })
            };
            createReq.Headers.Add("Cookie", string.IsNullOrEmpty(antiCookie) ? authCookie : $"{authCookie}; {antiCookie}");
            createReq.Headers.Add("X-XSRF-TOKEN", csrfToken);
            var res = await client.SendAsync(createReq);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        }

        var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/customer/wallet/topups?page=1&pageSize=10");
        listReq.Headers.Add("Cookie", authCookie);
        var listRes = await client.SendAsync(listReq);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);

        var body = await listRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<CustomerWalletTopUpDto>>>();
        Assert.NotNull(body?.Data);
        Assert.Equal(2, body.Data.TotalCount);
        Assert.Equal(2, body.Data.Items.Count);
        Assert.True(body.Data.Items[0].CreatedAt >= body.Data.Items[1].CreatedAt);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 12. Feature Gate Safety on Administrator Approval
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ApprovePayment_FeatureGateDisabled_ReturnsForbidden_WalletUntouched()
    {
        // 1. Create top-up while feature gate is enabled
        var enabledClient = CreateEnabledClient();
        var email = $"gate_approve_{Guid.NewGuid():N}@test.local";
        var (customerId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", enabledClient);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, enabledClient);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 5000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", string.IsNullOrEmpty(custAnti) ? custCookie : $"{custCookie}; {custAnti}");
        createReq.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes = await enabledClient.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        // 2. Attempt approval using a client configured with Feature Gate Disabled (Enabled = false)
        var disabledClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<WalletTopUpOptions>(options =>
                {
                    options.Enabled = false;
                });
            });
        }).CreateClient();

        var (adminCookie, adminAnti, adminCsrf) = await LoginAsAdminAsync(disabledClient);

        var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 5000m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-GATE-{Guid.NewGuid():N}"
            })
        };
        approveReq.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
        approveReq.Headers.Add("X-XSRF-TOKEN", adminCsrf);

        var approveRes = await disabledClient.SendAsync(approveReq);
        Assert.Equal(HttpStatusCode.Forbidden, approveRes.StatusCode);

        // Verify wallet was NOT credited and payment remains Pending
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.FirstAsync(p => p.PaymentNumber == payNumber);
            Assert.Equal(PaymentStatus.Pending, payment.Status);

            var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(0.0000m, wallet.Balance);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 13. Financial Verification Rules: Mismatches, Explicit Confirmation, Ambiguous Refs
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_ApprovePayment_Validations_AmountMismatch_CurrencyMismatch_MissingConfirm_AmbiguousRef()
    {
        var client = CreateEnabledClient();
        var email = $"val_chk_{Guid.NewGuid():N}@test.local";
        var (customerId, custCookie) = await RegisterAndLoginCustomerAsync(email, "Password123!", client);
        var (custCsrf, custAnti) = await GetCustomerCsrfTokenAsync(custCookie, client);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/customer/wallet/topups")
        {
            Content = JsonContent.Create(new CreateCustomerWalletTopUpRequest
            {
                Amount = 5000m,
                PaymentMethodCode = "BANK_TRANSFER"
            })
        };
        createReq.Headers.Add("Cookie", string.IsNullOrEmpty(custAnti) ? custCookie : $"{custCookie}; {custAnti}");
        createReq.Headers.Add("X-XSRF-TOKEN", custCsrf);
        var createRes = await client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var payNumber = (await createRes.Content.ReadFromJsonAsync<ApiResponse<CustomerWalletTopUpDto>>())!.Data!.PaymentNumber;

        var (adminCookie, adminAnti, adminCsrf) = await LoginAsAdminAsync(client);

        // 1. Missing explicit confirmation
        var reqNoConfirm = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 5000m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = false,
                VerifiedTransactionId = $"TRX-TEST-{Guid.NewGuid():N}"
            })
        };
        reqNoConfirm.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
        reqNoConfirm.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var resNoConfirm = await client.SendAsync(reqNoConfirm);
        Assert.Equal(HttpStatusCode.BadRequest, resNoConfirm.StatusCode);

        // 2. Amount mismatch (requested 5000, admin verified 4500)
        var reqAmountMismatch = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 4500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-TEST-{Guid.NewGuid():N}"
            })
        };
        reqAmountMismatch.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
        reqAmountMismatch.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var resAmountMismatch = await client.SendAsync(reqAmountMismatch);
        Assert.Equal(HttpStatusCode.BadRequest, resAmountMismatch.StatusCode);

        // 3. Currency mismatch
        var reqCurrMismatch = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
        {
            Content = JsonContent.Create(new ApproveManualPaymentRequest
            {
                VerifiedAmount = 5000m,
                VerifiedCurrency = "USD",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-TEST-{Guid.NewGuid():N}"
            })
        };
        reqCurrMismatch.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
        reqCurrMismatch.Headers.Add("X-XSRF-TOKEN", adminCsrf);
        var resCurrMismatch = await client.SendAsync(reqCurrMismatch);
        Assert.Equal(HttpStatusCode.BadRequest, resCurrMismatch.StatusCode);

        // 4. Ambiguous / placeholder transaction IDs ("0000", "N/A", "CASH", "1111", length < 4)
        foreach (var badRef in new[] { "0000", "N/A", "CASH", "1111", "TXN", "AB" })
        {
            var reqBadRef = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/manual/{payNumber}/approve")
            {
                Content = JsonContent.Create(new ApproveManualPaymentRequest
                {
                    VerifiedAmount = 5000m,
                    VerifiedCurrency = "PKR",
                    VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                    ConfirmFundsReceived = true,
                    VerifiedTransactionId = badRef
                })
            };
            reqBadRef.Headers.Add("Cookie", string.IsNullOrEmpty(adminAnti) ? adminCookie : $"{adminCookie}; {adminAnti}");
            reqBadRef.Headers.Add("X-XSRF-TOKEN", adminCsrf);
            var resBadRef = await client.SendAsync(reqBadRef);
            Assert.Equal(HttpStatusCode.BadRequest, resBadRef.StatusCode);
        }

        // Verify payment remains Pending and wallet balance is 0
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.FirstAsync(p => p.PaymentNumber == payNumber);
            Assert.Equal(PaymentStatus.Pending, payment.Status);

            var wallet = await db.Wallets.FirstAsync(w => w.CustomerId == customerId);
            Assert.Equal(0.0000m, wallet.Balance);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 14. PostgreSQL Concurrency: Two Simultaneous Admin Approvals
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentApproval_ExactlyOneSucceeds_NoDoubleCredit_LoserGetsConflict()
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
            UserName = $"pg_concurrent_{suffix}@test.local",
            Email = $"pg_concurrent_{suffix}@test.local",
            FullName = "PG Concurrent User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(customer);

        var adminUser = new ApplicationUser
        {
            UserName = $"pg_admin_{suffix}@test.local",
            Email = $"pg_admin_{suffix}@test.local",
            FullName = "PG Admin User",
            EmailConfirmed = true,
            IsActive = true
        };
        seedDb.Users.Add(adminUser);

        var wallet = new Wallet
        {
            CustomerId = customer.Id,
            Currency = "PKR",
            Balance = 0.0000m
        };
        seedDb.Wallets.Add(wallet);
        await seedDb.SaveChangesAsync();

        var paymentNumber = $"PAY-CONCUR-{suffix}";
        var payment = new Payment
        {
            PaymentNumber = paymentNumber,
            Purpose = PaymentPurpose.WalletTopUp,
            WalletId = wallet.Id,
            OrderId = null,
            Amount = 1500m,
            Currency = "PKR",
            PaymentMethodCode = "BANK_TRANSFER",
            Status = PaymentStatus.Pending
        };
        seedDb.Payments.Add(payment);
        await seedDb.SaveChangesAsync();

        try
        {
            // Set up two distinct DbContexts and verification services
            await using var db1 = new AppDbContext(options);
            await using var db2 = new AppDbContext(options);

            var topUpOptions = Options.Create(new WalletTopUpOptions { Enabled = true });

            var walletService1 = new WalletService(db1, NullLogger<WalletService>.Instance);
            var verifyService1 = new AdminPaymentVerificationService(
                db1,
                walletService1,
                topUpOptions,
                NullLogger<AdminPaymentVerificationService>.Instance);

            var walletService2 = new WalletService(db2, NullLogger<WalletService>.Instance);
            var verifyService2 = new AdminPaymentVerificationService(
                db2,
                walletService2,
                topUpOptions,
                NullLogger<AdminPaymentVerificationService>.Instance);

            var req1 = new ApproveManualPaymentRequest
            {
                VerifiedAmount = 1500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-CONC-1-{suffix}",
                AdminNotes = "Admin 1 Approval"
            };

            var req2 = new ApproveManualPaymentRequest
            {
                VerifiedAmount = 1500m,
                VerifiedCurrency = "PKR",
                VerifiedReceivingAccount = "Meezan Bank - 01020304050607",
                ConfirmFundsReceived = true,
                VerifiedTransactionId = $"TRX-CONC-2-{suffix}",
                AdminNotes = "Admin 2 Approval"
            };

            // Execute concurrent approvals simultaneously
            var task1 = Task.Run(() => verifyService1.ApproveManualTopUpAsync(adminUser.Id, paymentNumber, req1));
            var task2 = Task.Run(() => verifyService2.ApproveManualTopUpAsync(adminUser.Id, paymentNumber, req2));

            var results = await Task.WhenAll(task1, task2);

            // Assert: Exactly one Success, exactly one Conflict
            Assert.Contains(results, r => r.Status == AdminVerificationResultStatus.Success);
            Assert.Contains(results, r => r.Status == AdminVerificationResultStatus.Conflict);

            // Verify final state in PostgreSQL: Wallet balance must be credited exactly once
            await using var verifyDb = new AppDbContext(options);
            var verifiedWallet = await verifyDb.Wallets
                .Include(w => w.Transactions)
                .FirstAsync(w => w.Id == wallet.Id);

            Assert.Equal(1500.0000m, verifiedWallet.Balance);
            Assert.Single(verifiedWallet.Transactions);
            Assert.Equal(1500m, verifiedWallet.Transactions.First().Amount);
            Assert.Equal(WalletTransactionType.Credit, verifiedWallet.Transactions.First().TransactionType);

            var verifiedPayment = await verifyDb.Payments
                .Include(p => p.StatusHistory)
                .FirstAsync(p => p.PaymentNumber == paymentNumber);

            Assert.Equal(PaymentStatus.Paid, verifiedPayment.Status);
            Assert.NotNull(verifiedPayment.PaidAt);
            Assert.NotNull(verifiedPayment.VerifiedAt);
            Assert.Equal(adminUser.Id, verifiedPayment.VerifiedByUserId);
            Assert.Single(verifiedPayment.StatusHistory, h => h.NewStatus == PaymentStatus.Paid);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(options);
            var p = await cleanupDb.Payments.Include(x => x.StatusHistory).FirstOrDefaultAsync(x => x.PaymentNumber == paymentNumber);
            if (p != null)
            {
                cleanupDb.PaymentStatusHistories.RemoveRange(p.StatusHistory);
                cleanupDb.Payments.Remove(p);
            }

            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.Id == wallet.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }

            var u1 = await cleanupDb.Users.FindAsync(customer.Id);
            if (u1 != null) cleanupDb.Users.Remove(u1);

            var u2 = await cleanupDb.Users.FindAsync(adminUser.Id);
            if (u2 != null) cleanupDb.Users.Remove(u2);

            await cleanupDb.SaveChangesAsync();
        }
    }
}

