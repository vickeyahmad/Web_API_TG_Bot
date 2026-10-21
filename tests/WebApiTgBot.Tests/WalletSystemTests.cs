using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Tests;

/// <summary>
/// Automated integration tests verifying Customer PKR Wallet & Permanent Transaction Ledger (Milestone 4B):
/// - Zero initial balance and currency validation.
/// - Exactly one wallet per customer per currency.
/// - Concurrency control: row-level locking (SELECT ... FOR UPDATE) prevents lost updates and overspending.
/// - Durable idempotency: safe replays, conflict rejection on mismatched reuse.
/// - Non-negative balance constraint at both application and PostgreSQL engine level.
/// - Permanent append-only ledger: BalanceBefore/BalanceAfter audit trail and Restrict delete protection.
/// - Zero impact on existing Order and Payment statuses.
/// - Direct PostgreSQL verification against isolated webapitgbot_test database.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public class WalletSystemTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public WalletSystemTests()
    {
        _dbOptions = PostgreSqlTestDatabase.CreateOptions();
    }

    private async Task<AppDbContext> CreateDbContextAsync()
    {
        var db = new AppDbContext(_dbOptions);
        var connected = await PostgreSqlTestDatabase.EnsureMigratedAsync(db);
        Assert.True(connected, "PostgreSQL test database 'webapitgbot_test' must be reachable for financial wallet integration tests.");
        return db;
    }

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<ApplicationUser> CreateTestCustomerAsync(AppDbContext db, string suffix)
    {
        var customer = new ApplicationUser
        {
            UserName = $"wallet_cust_{suffix}@test.local".ToLowerInvariant(),
            Email = $"wallet_cust_{suffix}@test.local".ToLowerInvariant(),
            FullName = $"Wallet Customer {suffix}",
            EmailConfirmed = true,
            IsActive = true
        };
        db.Users.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1 & 2. Wallet Initialization & Uniqueness per Customer/Currency
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_Initialized_HasZeroBalanceAndPkrCurrency()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var wallet = await service.GetOrCreateWalletAsync(customer.Id, "PKR");

            Assert.NotNull(wallet);
            Assert.Equal(customer.Id, wallet.CustomerId);
            Assert.Equal("PKR", wallet.Currency);
            Assert.Equal(0.0000m, wallet.Balance);
            Assert.True(wallet.Id > 0);

            // Re-fetch via GetBalanceAsync
            var balance = await service.GetBalanceAsync(customer.Id, "PKR");
            Assert.Equal(0.0000m, balance);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null) cleanupDb.Wallets.Remove(w);
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_OneWalletPerCustomerAndCurrency_EnforcesUniqueness()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var wallet1 = await service.GetOrCreateWalletAsync(customer.Id, "PKR");
            var wallet2 = await service.GetOrCreateWalletAsync(customer.Id, "PKR");

            Assert.Equal(wallet1.Id, wallet2.Id);

            // Direct database attempt to insert duplicate (CustomerId, Currency) in PostgreSQL
            var duplicateWallet = new Wallet
            {
                CustomerId = customer.Id,
                Currency = "PKR",
                Balance = 0.0000m
            };
            db.Wallets.Add(duplicateWallet);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pgEx.SqlState);
            Assert.Contains("IX_Wallets_CustomerId_Currency", pgEx.ConstraintName ?? pgEx.Message);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null) cleanupDb.Wallets.Remove(w);
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_ConcurrentInitialization_CreatesOnlyOneWallet()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);

        try
        {
            // Execute 10 simultaneous calls to GetOrCreateWalletAsync across distinct DbContext instances
            var tasks = Enumerable.Range(0, 10).Select(async _ =>
            {
                await using var taskDb = new AppDbContext(_dbOptions);
                var taskService = new WalletService(taskDb, NullLogger<WalletService>.Instance);
                return await taskService.GetOrCreateWalletAsync(customer.Id, "PKR");
            });

            var results = await Task.WhenAll(tasks);
            var firstWalletId = results[0].Id;

            foreach (var w in results)
            {
                Assert.Equal(firstWalletId, w.Id);
            }

            // Exactly 1 wallet exists in PostgreSQL
            await using var verifyDb = new AppDbContext(_dbOptions);
            var count = await verifyDb.Wallets.CountAsync(w => w.CustomerId == customer.Id && w.Currency == "PKR");
            Assert.Equal(1, count);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null) cleanupDb.Wallets.Remove(w);
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3 & 4. Credits, Debits, and Validation
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_Credit_IncreasesBalanceAndReturnsSuccess()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var request = new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 5000.5000m,
                Currency = "PKR",
                OperationKey = $"OP_CREDIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification,
                SourceReferenceId = $"PAY-DEP-{suffix}",
                Description = "Verified Easypaisa deposit"
            };

            var result = await service.CreditAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal(WalletOperationStatus.Success, result.Status);
            Assert.NotNull(result.Transaction);
            Assert.Equal(5000.5000m, result.Transaction.Amount);
            Assert.Equal(0.0000m, result.Transaction.BalanceBefore);
            Assert.Equal(5000.5000m, result.Transaction.BalanceAfter);
            Assert.Equal(5000.5000m, result.Wallet?.Balance);

            // Verify in PostgreSQL
            await using var verifyDb = new AppDbContext(_dbOptions);
            var dbWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(5000.5000m, dbWallet.Balance);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_Debit_DecreasesBalanceAndReturnsSuccess()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initial credit
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 5000.0000m,
                Currency = "PKR",
                OperationKey = $"OP_CREDIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            // Debit
            var debitResult = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 2000.0000m,
                Currency = "PKR",
                OperationKey = $"OP_DEBIT_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment,
                SourceReferenceId = $"ORD-TEST-{suffix}",
                Description = "Payment for AI Tool subscription"
            });

            Assert.True(debitResult.IsSuccess);
            Assert.Equal(WalletOperationStatus.Success, debitResult.Status);
            Assert.NotNull(debitResult.Transaction);
            Assert.Equal(2000.0000m, debitResult.Transaction.Amount);
            Assert.Equal(5000.0000m, debitResult.Transaction.BalanceBefore);
            Assert.Equal(3000.0000m, debitResult.Transaction.BalanceAfter);
            Assert.Equal(3000.0000m, debitResult.Wallet?.Balance);

            // Verify in PostgreSQL
            await using var verifyDb = new AppDbContext(_dbOptions);
            var dbWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(3000.0000m, dbWallet.Balance);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_Debit_WithInsufficientFunds_IsRejected()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initial credit of 3000
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 3000.0000m,
                Currency = "PKR",
                OperationKey = $"OP_INIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            // Attempt debit of 5000 (exceeds balance)
            var debitResult = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 5000.0000m,
                Currency = "PKR",
                OperationKey = $"OP_OVERDEBIT_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });

            Assert.False(debitResult.IsSuccess);
            Assert.Equal(WalletOperationStatus.InsufficientFunds, debitResult.Status);
            Assert.Contains("Insufficient wallet balance", debitResult.Message);

            // Balance remains exactly 3000 in PostgreSQL
            await using var verifyDb = new AppDbContext(_dbOptions);
            var dbWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(3000.0000m, dbWallet.Balance);

            // No ledger entry created for the rejected debit
            var txExists = await verifyDb.WalletTransactions.AnyAsync(t => t.OperationKey == $"OP_OVERDEBIT_{suffix}");
            Assert.False(txExists);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_ZeroOrNegativeAmount_IsRejected()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var zeroCredit = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 0.0000m,
                Currency = "PKR",
                OperationKey = $"OP_ZERO_CRED_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.Equal(WalletOperationStatus.InvalidAmount, zeroCredit.Status);

            var negCredit = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = -500.0000m,
                Currency = "PKR",
                OperationKey = $"OP_NEG_CRED_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.Equal(WalletOperationStatus.InvalidAmount, negCredit.Status);

            var zeroDebit = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 0.0000m,
                Currency = "PKR",
                OperationKey = $"OP_ZERO_DEB_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });
            Assert.Equal(WalletOperationStatus.InvalidAmount, zeroDebit.Status);

            var negDebit = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = -100.0000m,
                Currency = "PKR",
                OperationKey = $"OP_NEG_DEB_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });
            Assert.Equal(WalletOperationStatus.InvalidAmount, negDebit.Status);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_WrongCurrency_IsRejected()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initialized PKR wallet
            await service.GetOrCreateWalletAsync(customer.Id, "PKR");

            // Attempt credit in USD
            var usdCredit = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 50m,
                Currency = "USD",
                OperationKey = $"OP_USD_CRED_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.Equal(WalletOperationStatus.InvalidCurrency, usdCredit.Status);

            // Attempt debit in USDT
            var usdtDebit = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 50m,
                Currency = "USDT",
                OperationKey = $"OP_USDT_DEB_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });
            Assert.Equal(WalletOperationStatus.InvalidCurrency, usdtDebit.Status);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var wallets = await cleanupDb.Wallets.Where(x => x.CustomerId == customer.Id).ToListAsync();
            cleanupDb.Wallets.RemoveRange(wallets);
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5 & 6. Permanent Ledger Audit & Balance History
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_Transactions_CreateCorrectLedgerEntries_AndAccurateBalances()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // 1. Credit 5000 -> Bal 0 -> 5000
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 5000m,
                Currency = "PKR",
                OperationKey = $"OP_STEP1_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            // 2. Debit 2000 -> Bal 5000 -> 3000
            await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 2000m,
                Currency = "PKR",
                OperationKey = $"OP_STEP2_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });

            // 3. Credit 1000 -> Bal 3000 -> 4000
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = $"OP_STEP3_{suffix}",
                SourceType = WalletTransactionSource.Refund
            });

            var wallet = await service.GetOrCreateWalletAsync(customer.Id, "PKR");
            var history = await service.GetTransactionHistoryAsync(wallet.Id);

            Assert.Equal(3, history.Count);

            // History is ordered descending by CreatedAt/Id
            var tx3 = history.First(t => t.OperationKey == $"OP_STEP3_{suffix}");
            Assert.Equal(WalletTransactionType.Credit, tx3.TransactionType);
            Assert.Equal(1000m, tx3.Amount);
            Assert.Equal(3000m, tx3.BalanceBefore);
            Assert.Equal(4000m, tx3.BalanceAfter);

            var tx2 = history.First(t => t.OperationKey == $"OP_STEP2_{suffix}");
            Assert.Equal(WalletTransactionType.Debit, tx2.TransactionType);
            Assert.Equal(2000m, tx2.Amount);
            Assert.Equal(5000m, tx2.BalanceBefore);
            Assert.Equal(3000m, tx2.BalanceAfter);

            var tx1 = history.First(t => t.OperationKey == $"OP_STEP1_{suffix}");
            Assert.Equal(WalletTransactionType.Credit, tx1.TransactionType);
            Assert.Equal(5000m, tx1.Amount);
            Assert.Equal(0m, tx1.BalanceBefore);
            Assert.Equal(5000m, tx1.BalanceAfter);

            // Reconciliation check: Sum(Credits) - Sum(Debits) == Balance
            var sumCredits = history.Where(t => t.TransactionType == WalletTransactionType.Credit).Sum(t => t.Amount);
            var sumDebits = history.Where(t => t.TransactionType == WalletTransactionType.Debit).Sum(t => t.Amount);
            Assert.Equal(wallet.Balance, sumCredits - sumDebits);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_BalanceAndLedgerChanges_RollbackTogetherOnFailure()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initial state: Credit 1000m
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = $"OP_INIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            // Simulate explicit transactional failure: attempt credit but abort transaction
            await using var txDb = new AppDbContext(_dbOptions);
            var wallet = await txDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            await using (var tx = await txDb.Database.BeginTransactionAsync())
            {
                wallet.Balance += 5000m;
                var dummyTx = new WalletTransaction
                {
                    WalletId = wallet.Id,
                    TransactionType = WalletTransactionType.Credit,
                    Amount = 5000m,
                    Currency = "PKR",
                    BalanceBefore = 1000m,
                    BalanceAfter = 6000m,
                    OperationKey = $"OP_FAIL_{suffix}",
                    SourceType = WalletTransactionSource.DepositVerification,
                    CreatedAt = DateTime.UtcNow
                };
                txDb.WalletTransactions.Add(dummyTx);
                await txDb.SaveChangesAsync();

                // Explicit rollback
                await tx.RollbackAsync();
            }

            // Verify: Balance remains 1000m and OP_FAIL transaction does not exist
            await using var verifyDb = new AppDbContext(_dbOptions);
            var verifiedWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(1000m, verifiedWallet.Balance);

            var failTxExists = await verifyDb.WalletTransactions.AnyAsync(t => t.OperationKey == $"OP_FAIL_{suffix}");
            Assert.False(failTxExists);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7 & 8. Durable Idempotency Enforcement
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_RepeatedCredit_WithSameOperationKey_DoesNotDoubleCredit()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var request = new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1500m,
                Currency = "PKR",
                OperationKey = $"OP_REPEAT_CRED_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            };

            var firstCall = await service.CreditAsync(request);
            Assert.True(firstCall.IsSuccess);
            Assert.False(firstCall.IsReplay);
            Assert.Equal(1500m, firstCall.Wallet?.Balance);

            // Re-run exact same credit with same operation key
            var secondCall = await service.CreditAsync(request);
            Assert.True(secondCall.IsSuccess);
            Assert.True(secondCall.IsReplay);
            Assert.Equal(1500m, secondCall.Wallet?.Balance);

            // PostgreSQL has exactly ONE transaction and balance is 1500, not 3000
            await using var verifyDb = new AppDbContext(_dbOptions);
            var wallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(1500m, wallet.Balance);

            var txCount = await verifyDb.WalletTransactions.CountAsync(t => t.OperationKey == request.OperationKey);
            Assert.Equal(1, txCount);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_RepeatedDebit_WithSameOperationKey_DoesNotDoubleDebit_EvenAtZeroBalance()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initial credit of 1000m
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = $"OP_CRED_INIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            var debitRequest = new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m, // Empties the wallet completely
                Currency = "PKR",
                OperationKey = $"OP_REPEAT_DEB_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            };

            var firstCall = await service.DebitAsync(debitRequest);
            Assert.True(firstCall.IsSuccess);
            Assert.False(firstCall.IsReplay);
            Assert.Equal(0m, firstCall.Wallet?.Balance);

            // Repeating the exact same debit when balance is currently 0 must safely return replay,
            // NOT throw InsufficientFunds!
            var secondCall = await service.DebitAsync(debitRequest);
            Assert.True(secondCall.IsSuccess);
            Assert.True(secondCall.IsReplay);
            Assert.Equal(0m, secondCall.Wallet?.Balance);

            // PostgreSQL has exactly ONE debit transaction
            await using var verifyDb = new AppDbContext(_dbOptions);
            var txCount = await verifyDb.WalletTransactions.CountAsync(t => t.OperationKey == debitRequest.OperationKey);
            Assert.Equal(1, txCount);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_SameOperationKey_WithConflictingInput_IsRejected()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var key = $"OP_CONFLICT_{suffix}";

            // 1. Initial credit of 1000m
            var first = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = key,
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.True(first.IsSuccess);

            // 2. Reusing same key for a different amount (2000m)
            var mismatchAmount = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 2000m, // Different amount!
                Currency = "PKR",
                OperationKey = key,
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.Equal(WalletOperationStatus.Conflict, mismatchAmount.Status);

            // 3. Reusing same key for a debit
            var mismatchType = await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = key, // Reused for Debit!
                SourceType = WalletTransactionSource.OrderPayment
            });
            Assert.Equal(WalletOperationStatus.Conflict, mismatchType.Status);

            // Balance remains 1000m
            await using var verifyDb = new AppDbContext(_dbOptions);
            var wallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(1000m, wallet.Balance);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 9. Real PostgreSQL Concurrency Tests (Row Locking & Atomicity)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_TwoSimultaneousDebits_CannotOverspendWallet()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            // Initial balance: 1000m
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = $"OP_CONC_INIT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            // Two simultaneous tasks attempt to debit 800m each (Total 1600m > 1000m)
            var task1 = Task.Run(async () =>
            {
                await using var db1 = new AppDbContext(_dbOptions);
                var s1 = new WalletService(db1, NullLogger<WalletService>.Instance);
                return await s1.DebitAsync(new WalletDebitRequest
                {
                    CustomerId = customer.Id,
                    Amount = 800m,
                    Currency = "PKR",
                    OperationKey = $"OP_RACE_DEB1_{suffix}",
                    SourceType = WalletTransactionSource.OrderPayment
                });
            });

            var task2 = Task.Run(async () =>
            {
                await using var db2 = new AppDbContext(_dbOptions);
                var s2 = new WalletService(db2, NullLogger<WalletService>.Instance);
                return await s2.DebitAsync(new WalletDebitRequest
                {
                    CustomerId = customer.Id,
                    Amount = 800m,
                    Currency = "PKR",
                    OperationKey = $"OP_RACE_DEB2_{suffix}",
                    SourceType = WalletTransactionSource.OrderPayment
                });
            });

            var results = await Task.WhenAll(task1, task2);
            var successCount = results.Count(r => r.IsSuccess);
            var failCount = results.Count(r => r.Status == WalletOperationStatus.InsufficientFunds);

            // Exactly ONE succeeds and exactly ONE fails with InsufficientFunds
            Assert.Equal(1, successCount);
            Assert.Equal(1, failCount);

            // Final balance in PostgreSQL is exactly 200m (1000 - 800), NEVER negative
            await using var verifyDb = new AppDbContext(_dbOptions);
            var finalWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(200m, finalWallet.Balance);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_ConcurrentDistinctCredits_ProduceCorrectFinalBalance()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);

        try
        {
            // 5 concurrent credits of 1000m each across distinct DbContexts
            var tasks = Enumerable.Range(1, 5).Select(i => Task.Run(async () =>
            {
                await using var taskDb = new AppDbContext(_dbOptions);
                var s = new WalletService(taskDb, NullLogger<WalletService>.Instance);
                return await s.CreditAsync(new WalletCreditRequest
                {
                    CustomerId = customer.Id,
                    Amount = 1000m,
                    Currency = "PKR",
                    OperationKey = $"OP_CONC_CRED_{i}_{suffix}",
                    SourceType = WalletTransactionSource.DepositVerification
                });
            }));

            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.True(r.IsSuccess));

            // Final balance must be exactly 5000m with 0 lost updates
            await using var verifyDb = new AppDbContext(_dbOptions);
            var finalWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(5000m, finalWallet.Balance);

            var txCount = await verifyDb.WalletTransactions.CountAsync(t => t.WalletId == finalWallet.Id);
            Assert.Equal(5, txCount);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_DuplicateConcurrentRequests_ProduceOneMutation()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var sharedKey = $"OP_SHARED_RACE_{suffix}";

        try
        {
            // 3 concurrent requests submitting the identical credit request with the same OperationKey
            var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                await using var taskDb = new AppDbContext(_dbOptions);
                var s = new WalletService(taskDb, NullLogger<WalletService>.Instance);
                return await s.CreditAsync(new WalletCreditRequest
                {
                    CustomerId = customer.Id,
                    Amount = 1000m,
                    Currency = "PKR",
                    OperationKey = sharedKey,
                    SourceType = WalletTransactionSource.DepositVerification
                });
            }));

            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.True(r.IsSuccess));

            // Final balance is credited only ONCE (1000m, not 3000m)
            await using var verifyDb = new AppDbContext(_dbOptions);
            var finalWallet = await verifyDb.Wallets.FirstAsync(w => w.CustomerId == customer.Id);
            Assert.Equal(1000m, finalWallet.Balance);

            // Exactly 1 transaction exists in PostgreSQL
            var txCount = await verifyDb.WalletTransactions.CountAsync(t => t.OperationKey == sharedKey);
            Assert.Equal(1, txCount);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 10. Relational Integrity & Standalone Isolation
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql_HistoricalLedger_NotDeletedThroughCascadingDeletes()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        try
        {
            var creditRes = await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = "PKR",
                OperationKey = $"OP_RESTRICT_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });
            Assert.True(creditRes.IsSuccess);

            // 1. Attempting to remove the Wallet while transactions are tracked in memory
            // is rejected by EF Core with InvalidOperationException due to DeleteBehavior.Restrict
            var walletToDelete = await db.Wallets.FindAsync(creditRes.Wallet!.Id);
            Assert.NotNull(walletToDelete);
            Assert.Throws<InvalidOperationException>(() => db.Wallets.Remove(walletToDelete));

            // 2. In a fresh context without tracked child entries, attempting to delete the Wallet
            // is rejected by PostgreSQL with 23503 foreign key violation due to ON DELETE RESTRICT
            await using (var deleteDb = new AppDbContext(_dbOptions))
            {
                var wToDelete = await deleteDb.Wallets.FindAsync(creditRes.Wallet!.Id);
                Assert.NotNull(wToDelete);
                deleteDb.Wallets.Remove(wToDelete);

                var exWallet = await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
                var pgExWallet = Assert.IsType<PostgresException>(exWallet.InnerException);
                Assert.Equal("23503", pgExWallet.SqlState);
            }

            // 3. Attempting to delete the Customer while a Wallet exists
            // is rejected by PostgreSQL with 23503 foreign key violation due to ON DELETE RESTRICT
            await using (var deleteDb = new AppDbContext(_dbOptions))
            {
                var custToDelete = await deleteDb.Users.FindAsync(customer.Id);
                Assert.NotNull(custToDelete);
                deleteDb.Users.Remove(custToDelete);

                var exCustomer = await Assert.ThrowsAsync<DbUpdateException>(() => deleteDb.SaveChangesAsync());
                var pgExCustomer = Assert.IsType<PostgresException>(exCustomer.InnerException);
                Assert.Equal("23503", pgExCustomer.SqlState);
            }
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }
            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);
            await cleanupDb.SaveChangesAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Wallet_StandaloneOperations_DoNotMutateExistingOrdersOrPayments()
    {
        await using var db = await CreateDbContextAsync();
        var suffix = NewSuffix();
        var customer = await CreateTestCustomerAsync(db, suffix);
        var service = new WalletService(db, NullLogger<WalletService>.Instance);

        // 1. Create an Order in PendingPayment status
        var order = new Order
        {
            OrderNumber = $"ORD-WALISOL-{suffix}",
            CustomerId = customer.Id,
            Status = OrderStatus.PendingPayment,
            Currency = "PKR",
            Subtotal = 5000m,
            TotalAmount = 5000m,
            SalesChannel = SalesChannel.Website
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // 2. Create a Payment attempt in Pending status
        var payment = new Payment
        {
            PaymentNumber = $"PAY-WALISOL-{suffix}",
            OrderId = order.Id,
            Amount = 5000m,
            Currency = "PKR",
            PaymentMethodCode = PaymentMethodCodes.Wallet,
            Status = PaymentStatus.Pending
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        try
        {
            // 3. Perform unrelated wallet operations
            await service.CreditAsync(new WalletCreditRequest
            {
                CustomerId = customer.Id,
                Amount = 10000m,
                Currency = "PKR",
                OperationKey = $"OP_ISOL_CRED_{suffix}",
                SourceType = WalletTransactionSource.DepositVerification
            });

            await service.DebitAsync(new WalletDebitRequest
            {
                CustomerId = customer.Id,
                Amount = 2500m,
                Currency = "PKR",
                OperationKey = $"OP_ISOL_DEB_{suffix}",
                SourceType = WalletTransactionSource.OrderPayment
            });

            // 4. Assert: Order status remains PendingPayment, Payment status remains Pending
            await using var verifyDb = new AppDbContext(_dbOptions);
            var verifiedOrder = await verifyDb.Orders.FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderStatus.PendingPayment, verifiedOrder.Status);

            var verifiedPayment = await verifyDb.Payments.FirstAsync(p => p.Id == payment.Id);
            Assert.Equal(PaymentStatus.Pending, verifiedPayment.Status);
        }
        finally
        {
            await using var cleanupDb = new AppDbContext(_dbOptions);
            var p = await cleanupDb.Payments.FirstOrDefaultAsync(x => x.Id == payment.Id);
            if (p != null) cleanupDb.Payments.Remove(p);

            var o = await cleanupDb.Orders.FirstOrDefaultAsync(x => x.Id == order.Id);
            if (o != null) cleanupDb.Orders.Remove(o);

            var w = await cleanupDb.Wallets.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.CustomerId == customer.Id);
            if (w != null)
            {
                cleanupDb.WalletTransactions.RemoveRange(w.Transactions);
                cleanupDb.Wallets.Remove(w);
            }

            var u = await cleanupDb.Users.FindAsync(customer.Id);
            if (u != null) cleanupDb.Users.Remove(u);

            await cleanupDb.SaveChangesAsync();
        }
    }
}
