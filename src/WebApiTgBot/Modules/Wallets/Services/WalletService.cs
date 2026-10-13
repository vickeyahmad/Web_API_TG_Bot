using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Modules.Wallets.Services;

/// <summary>
/// Centralized implementation of internal wallet balance management and the permanent transaction ledger.
/// Enforces pessimistic row-level locking (SELECT ... FOR UPDATE) in PostgreSQL to eliminate concurrency race conditions.
/// Enforces durable idempotency, strict currency matching, and non-negative balances.
/// </summary>
public class WalletService : IWalletService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WalletService> _logger;

    public WalletService(AppDbContext db, ILogger<WalletService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Wallet> GetOrCreateWalletAsync(string customerId, string currency = "PKR", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            throw new ArgumentException("CustomerId cannot be empty.", nameof(customerId));
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "PKR" : currency.Trim().ToUpperInvariant();

        if (!string.Equals(normalizedCurrency, "PKR", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Only 'PKR' currency is currently supported. Received '{normalizedCurrency}'.", nameof(currency));
        }

        // 1. Check if wallet already exists
        var existing = await _db.Wallets
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.Currency == normalizedCurrency, ct);

        if (existing != null)
        {
            return existing;
        }

        // 2. Validate customer exists in the identity database
        var customerExists = await _db.Users.AnyAsync(u => u.Id == customerId, ct);
        if (!customerExists)
        {
            throw new KeyNotFoundException($"Customer with ID '{customerId}' does not exist.");
        }

        // 3. Attempt initialization with zero balance
        var newWallet = new Wallet
        {
            CustomerId = customerId,
            Currency = normalizedCurrency,
            Balance = 0.0000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Wallets.Add(newWallet);

        try
        {
            await _db.SaveChangesAsync(ct);
            return newWallet;
        }
        catch (DbUpdateException ex)
        {
            // Handle concurrent race where another thread/process inserted the wallet simultaneously
            _db.ChangeTracker.Clear();
            var reloaded = await _db.Wallets
                .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.Currency == normalizedCurrency, ct);

            if (reloaded != null)
            {
                return reloaded;
            }

            _logger.LogError(ex, "Failed to create wallet for customer {CustomerId} and currency {Currency}", customerId, normalizedCurrency);
            throw;
        }
    }

    public async Task<decimal?> GetBalanceAsync(string customerId, string currency = "PKR", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return null;
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(currency) ? "PKR" : currency.Trim().ToUpperInvariant();

        var wallet = await _db.Wallets
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.CustomerId == customerId && w.Currency == normalizedCurrency, ct);

        return wallet?.Balance;
    }

    public async Task<WalletOperationResult> CreditAsync(WalletCreditRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return WalletOperationResult.NotFound("CustomerId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OperationKey))
        {
            return WalletOperationResult.Conflict("OperationKey is required.");
        }

        if (request.Amount <= 0)
        {
            return WalletOperationResult.InvalidAmount("Amount must be greater than zero.");
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(request.Currency) ? "PKR" : request.Currency.Trim().ToUpperInvariant();

        if (!string.Equals(normalizedCurrency, "PKR", StringComparison.OrdinalIgnoreCase))
        {
            return WalletOperationResult.InvalidCurrency($"Only 'PKR' currency is currently supported. Received '{normalizedCurrency}'.");
        }

        if (!WalletTransactionSource.IsValid(request.SourceType))
        {
            return WalletOperationResult.Failed($"Invalid transaction source type '{request.SourceType}'.");
        }

        // Validate customer account
        var customer = await _db.Users.FindAsync(new object[] { request.CustomerId }, ct);
        if (customer == null)
        {
            return WalletOperationResult.NotFound($"Customer with ID '{request.CustomerId}' was not found.");
        }

        if (!customer.IsActive)
        {
            return WalletOperationResult.Failed("Customer account is inactive.");
        }

        var wallet = await GetOrCreateWalletAsync(request.CustomerId, normalizedCurrency, ct);

        return await ExecuteMutationAsync(
            walletId: wallet.Id,
            transactionType: WalletTransactionType.Credit,
            amount: request.Amount,
            currency: normalizedCurrency,
            operationKey: request.OperationKey.Trim(),
            sourceType: request.SourceType.Trim(),
            sourceReferenceId: request.SourceReferenceId?.Trim(),
            description: request.Description?.Trim(),
            createdByUserId: request.CreatedByUserId,
            ct: ct);
    }

    public async Task<WalletOperationResult> DebitAsync(WalletDebitRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return WalletOperationResult.NotFound("CustomerId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OperationKey))
        {
            return WalletOperationResult.Conflict("OperationKey is required.");
        }

        if (request.Amount <= 0)
        {
            return WalletOperationResult.InvalidAmount("Amount must be greater than zero.");
        }

        var normalizedCurrency = string.IsNullOrWhiteSpace(request.Currency) ? "PKR" : request.Currency.Trim().ToUpperInvariant();

        if (!string.Equals(normalizedCurrency, "PKR", StringComparison.OrdinalIgnoreCase))
        {
            return WalletOperationResult.InvalidCurrency($"Only 'PKR' currency is currently supported. Received '{normalizedCurrency}'.");
        }

        if (!WalletTransactionSource.IsValid(request.SourceType))
        {
            return WalletOperationResult.Failed($"Invalid transaction source type '{request.SourceType}'.");
        }

        // Validate customer account
        var customer = await _db.Users.FindAsync(new object[] { request.CustomerId }, ct);
        if (customer == null)
        {
            return WalletOperationResult.NotFound($"Customer with ID '{request.CustomerId}' was not found.");
        }

        if (!customer.IsActive)
        {
            return WalletOperationResult.Failed("Customer account is inactive.");
        }

        var wallet = await GetOrCreateWalletAsync(request.CustomerId, normalizedCurrency, ct);

        return await ExecuteMutationAsync(
            walletId: wallet.Id,
            transactionType: WalletTransactionType.Debit,
            amount: request.Amount,
            currency: normalizedCurrency,
            operationKey: request.OperationKey.Trim(),
            sourceType: request.SourceType.Trim(),
            sourceReferenceId: request.SourceReferenceId?.Trim(),
            description: request.Description?.Trim(),
            createdByUserId: request.CreatedByUserId,
            ct: ct);
    }

    public async Task<IReadOnlyList<WalletTransaction>> GetTransactionHistoryAsync(long walletId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        return await _db.WalletTransactions
            .AsNoTracking()
            .Where(t => t.WalletId == walletId)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Internal Atomic Mutation Engine
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<WalletOperationResult> ExecuteMutationAsync(
        long walletId,
        WalletTransactionType transactionType,
        decimal amount,
        string currency,
        string operationKey,
        string sourceType,
        string? sourceReferenceId,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational())
        {
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        try
        {
            Wallet? lockedWallet;
            if (_db.Database.IsRelational())
            {
                // Clear any pre-existing tracked entity so FromSqlInterpolated reads
                // the fresh, committed database state locked under SELECT ... FOR UPDATE
                _db.ChangeTracker.Clear();
                lockedWallet = await _db.Wallets
                    .FromSqlInterpolated($"SELECT * FROM \"Wallets\" WHERE \"Id\" = {walletId} FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                lockedWallet = await _db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);
            }

            if (lockedWallet == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOperationResult.NotFound($"Wallet with ID {walletId} was not found.");
            }

            // 2. Durable Idempotency Check BEFORE checking debit balance
            // If this is a replay of an operation that previously caused a balance change (e.g. balance was reduced to 0),
            // checking idempotency first ensures we return the original successful transaction rather than 'InsufficientFunds'.
            var existingTx = await _db.WalletTransactions
                .FirstOrDefaultAsync(t => t.WalletId == lockedWallet.Id && t.OperationKey == operationKey, ct);

            if (existingTx != null)
            {
                if (existingTx.TransactionType == transactionType &&
                    existingTx.Amount == amount &&
                    string.Equals(existingTx.Currency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    if (tx != null) await tx.RollbackAsync(ct);
                    return WalletOperationResult.Succeeded(lockedWallet, existingTx, isReplay: true);
                }

                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOperationResult.Conflict(
                    $"Operation key '{operationKey}' was already used for a different transaction.");
            }

            // 3. Strict Currency Validation
            if (!string.Equals(lockedWallet.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOperationResult.InvalidCurrency(
                    $"Wallet currency is '{lockedWallet.Currency}', but request specified '{currency}'. Cross-currency operations are prohibited.");
            }

            // 4. Balance Verification for Debits
            if (transactionType == WalletTransactionType.Debit && lockedWallet.Balance < amount)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return WalletOperationResult.InsufficientFunds(lockedWallet.Balance, amount, lockedWallet.Currency);
            }

            // 5. Compute new balance
            var balanceBefore = lockedWallet.Balance;
            var balanceAfter = transactionType == WalletTransactionType.Credit
                ? balanceBefore + amount
                : balanceBefore - amount;

            lockedWallet.Balance = balanceAfter;
            lockedWallet.UpdatedAt = DateTime.UtcNow;

            // 6. Record immutable permanent ledger transaction
            var ledgerEntry = new WalletTransaction
            {
                WalletId = lockedWallet.Id,
                TransactionType = transactionType,
                Amount = amount,
                Currency = currency,
                BalanceBefore = balanceBefore,
                BalanceAfter = balanceAfter,
                OperationKey = operationKey,
                SourceType = sourceType,
                SourceReferenceId = sourceReferenceId,
                Description = description,
                CreatedByUserId = createdByUserId,
                CreatedAt = DateTime.UtcNow
            };

            _db.WalletTransactions.Add(ledgerEntry);

            // 7. Commit atomically
            await _db.SaveChangesAsync(ct);

            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation(
                "Wallet {WalletId} {MutationType} of {Amount} {Currency} committed. New balance: {BalanceAfter} (OpKey: {OpKey})",
                lockedWallet.Id, transactionType, amount, currency, balanceAfter, operationKey);

            return WalletOperationResult.Succeeded(lockedWallet, ledgerEntry, isReplay: false);
        }
        catch (DbUpdateException ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            // In the event of a concurrent race where the unique index caught a duplicate OperationKey
            if (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _db.ChangeTracker.Clear();
                var existingTx = await _db.WalletTransactions
                    .FirstOrDefaultAsync(t => t.WalletId == walletId && t.OperationKey == operationKey, ct);

                if (existingTx != null)
                {
                    if (existingTx.TransactionType == transactionType &&
                        existingTx.Amount == amount &&
                        string.Equals(existingTx.Currency, currency, StringComparison.OrdinalIgnoreCase))
                    {
                        var reloadedWallet = await _db.Wallets.FindAsync(new object[] { walletId }, ct);
                        return WalletOperationResult.Succeeded(reloadedWallet!, existingTx, isReplay: true);
                    }

                    return WalletOperationResult.Conflict(
                        $"Operation key '{operationKey}' was already used for a different transaction.");
                }
            }

            _logger.LogError(ex, "Database update error during wallet mutation for wallet {WalletId}", walletId);
            throw;
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Unexpected error during wallet mutation for wallet {WalletId}", walletId);
            throw;
        }
        finally
        {
            if (tx != null)
            {
                await tx.DisposeAsync();
            }
        }
    }
}
