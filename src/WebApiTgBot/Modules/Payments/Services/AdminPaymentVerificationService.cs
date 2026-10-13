using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Data;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Wallets.Models;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Service implementing administrator review, rejection, and atomic approval of manual payment top-ups.
/// Guarantees that approving a payment and crediting the PKR wallet executes inside a single atomic PostgreSQL transaction.
/// </summary>
public class AdminPaymentVerificationService : IAdminPaymentVerificationService
{
    private readonly AppDbContext _db;
    private readonly IWalletService _walletService;
    private readonly WalletTopUpOptions _options;
    private readonly ILogger<AdminPaymentVerificationService> _logger;

    public AdminPaymentVerificationService(
        AppDbContext db,
        IWalletService walletService,
        IOptions<WalletTopUpOptions> options,
        ILogger<AdminPaymentVerificationService> logger)
    {
        _db = db;
        _walletService = walletService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PagedResult<AdminManualPaymentDto>> GetManualPaymentsAsync(
        PaymentStatus? status = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.Payments
            .AsNoTracking()
            .Include(p => p.Wallet)
                .ThenInclude(w => w!.Customer)
            .Include(p => p.VerifiedByUser)
            .Include(p => p.StatusHistory)
                .ThenInclude(h => h.ChangedByUser)
            .Where(p => p.Purpose == PaymentPurpose.WalletTopUp);

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        var total = await query.CountAsync(ct);

        var payments = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = payments.Select(MapToAdminDto).ToList();
        return new PagedResult<AdminManualPaymentDto>(dtos, page, pageSize, total);
    }

    public async Task<AdminManualPaymentDto?> GetManualPaymentByNumberAsync(
        string paymentNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            return null;
        }

        var payment = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Wallet)
                .ThenInclude(w => w!.Customer)
            .Include(p => p.VerifiedByUser)
            .Include(p => p.StatusHistory)
                .ThenInclude(h => h.ChangedByUser)
            .FirstOrDefaultAsync(p => p.PaymentNumber == paymentNumber.Trim() &&
                                      p.Purpose == PaymentPurpose.WalletTopUp, ct);

        return payment == null ? null : MapToAdminDto(payment);
    }

    public async Task<AdminVerificationResult> ApproveManualTopUpAsync(
        string adminUserId,
        string paymentNumber,
        ApproveManualPaymentRequest request,
        CancellationToken ct = default)
    {
        // 0. Feature Gate Check — Prohibits wallet credit when top-ups are disabled
        if (!_options.Enabled)
        {
            return AdminVerificationResult.Forbidden("Wallet top-up processing is currently disabled by server configuration.");
        }

        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return AdminVerificationResult.BadRequest("Admin user ID is required.");
        }

        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            return AdminVerificationResult.BadRequest("PaymentNumber is required.");
        }

        // 1. Mandatory Explicit Confirmation Check
        if (!request.ConfirmFundsReceived)
        {
            return AdminVerificationResult.BadRequest("Administrator must explicitly confirm that funds were independently verified in the receiving bank account.");
        }

        // 2. Receiving Account Validation
        if (string.IsNullOrWhiteSpace(request.VerifiedReceivingAccount))
        {
            return AdminVerificationResult.BadRequest("VerifiedReceivingAccount is required.");
        }

        var normalizedAccount = request.VerifiedReceivingAccount.Trim().ToUpperInvariant();
        if (normalizedAccount.Length < 3)
        {
            return AdminVerificationResult.BadRequest("VerifiedReceivingAccount must be at least 3 characters.");
        }

        // 3. Verified Transaction Reference Validation
        if (string.IsNullOrWhiteSpace(request.VerifiedTransactionId))
        {
            return AdminVerificationResult.BadRequest("VerifiedTransactionId is required.");
        }

        var normalizedTrxId = request.VerifiedTransactionId.Trim().ToUpperInvariant();
        if (IsUnreliableReference(normalizedTrxId))
        {
            return AdminVerificationResult.BadRequest("Transaction reference is too generic or ambiguous to establish reliable transaction identity. Manual investigation required.");
        }

        // 4. Amount and Currency Validation
        if (request.VerifiedAmount <= 0)
        {
            return AdminVerificationResult.BadRequest("VerifiedAmount must be greater than zero.");
        }

        var normalizedCurrency = request.VerifiedCurrency?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCurrency) || !string.Equals(normalizedCurrency, "PKR", StringComparison.OrdinalIgnoreCase))
        {
            return AdminVerificationResult.BadRequest($"Verified currency '{request.VerifiedCurrency}' is not supported. Only 'PKR' is supported.");
        }

        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational())
        {
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        try
        {
            // 5. Acquire pessimistic row-level lock on the Payment entity in PostgreSQL
            Payment? payment;
            if (_db.Database.IsRelational())
            {
                payment = await _db.Payments
                    .FromSqlInterpolated($"SELECT * FROM \"Payments\" WHERE \"PaymentNumber\" = {paymentNumber.Trim()} AND \"Purpose\" = 'WalletTopUp' FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                payment = await _db.Payments
                    .FirstOrDefaultAsync(p => p.PaymentNumber == paymentNumber.Trim() && p.Purpose == PaymentPurpose.WalletTopUp, ct);
            }

            if (payment == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.NotFound($"Top-up payment '{paymentNumber}' was not found.");
            }

            // Ensure navigation properties are loaded
            await _db.Entry(payment).Reference(p => p.Wallet).LoadAsync(ct);
            await _db.Entry(payment).Collection(p => p.StatusHistory).LoadAsync(ct);

            if (payment.Wallet == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.BadRequest("Payment is not associated with a valid customer wallet.");
            }

            // 6. Validate current status
            if (payment.Status == PaymentStatus.Paid)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.Conflict($"Payment '{paymentNumber}' has already been approved and credited.");
            }

            if (payment.Status != PaymentStatus.Pending)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.BadRequest($"Cannot approve payment in '{payment.Status}' status. Only Pending payments can be approved.");
            }

            // 7. Verify Amount and Currency Match Payment Request
            if (request.VerifiedAmount != payment.Amount)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.BadRequest($"Verified amount ({request.VerifiedAmount:F2} PKR) does not match requested top-up amount ({payment.Amount:F2} PKR). Approval rejected.");
            }

            if (!string.Equals(normalizedCurrency, payment.Currency, StringComparison.OrdinalIgnoreCase))
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.BadRequest($"Verified currency '{normalizedCurrency}' does not match payment currency '{payment.Currency}'. Approval rejected.");
            }

            // 8. Uniqueness Check for Verified External Reference within Receiving Account
            var duplicateTrx = await _db.Payments
                .AnyAsync(p => p.Status == PaymentStatus.Paid &&
                               p.VerifiedReceivingAccount == normalizedAccount &&
                               p.ProviderTransactionId == normalizedTrxId &&
                               p.Id != payment.Id, ct);

            if (duplicateTrx)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.Conflict($"External transaction reference '{normalizedTrxId}' in receiving account '{normalizedAccount}' has already been used to approve another payment.");
            }

            // 9. Update Payment record to Paid with normalized verification values
            payment.Status = PaymentStatus.Paid;
            payment.PaidAt = DateTime.UtcNow;
            payment.ProviderTransactionId = normalizedTrxId;
            payment.VerifiedReceivingAccount = normalizedAccount;
            payment.VerifiedByUserId = adminUserId;
            payment.AdminNotes = request.AdminNotes?.Trim();
            payment.VerifiedAt = DateTime.UtcNow;
            payment.UpdatedAt = DateTime.UtcNow;

            // 10. Append PaymentStatusHistory entry
            var historyEntry = new PaymentStatusHistory
            {
                PaymentId = payment.Id,
                PreviousStatus = PaymentStatus.Pending,
                NewStatus = PaymentStatus.Paid,
                Reason = $"Approved by administrator (Ref: {normalizedTrxId}, Account: {normalizedAccount}, Amount: {request.VerifiedAmount:F2} PKR).",
                ChangedByUserId = adminUserId,
                ChangedAt = DateTime.UtcNow
            };
            payment.StatusHistory.Add(historyEntry);

            // 11. Credit Customer's PKR Wallet using stable operation key WALLET_TOPUP:{PaymentId}
            var operationKey = $"WALLET_TOPUP:{payment.Id}";
            var creditRequest = new WalletCreditRequest
            {
                CustomerId = payment.Wallet.CustomerId,
                Amount = payment.Amount,
                Currency = payment.Currency,
                OperationKey = operationKey,
                SourceType = WalletTransactionSource.DepositVerification,
                SourceReferenceId = payment.PaymentNumber,
                Description = $"Manual wallet top-up approved (TRX: {normalizedTrxId})",
                CreatedByUserId = adminUserId
            };

            var creditResult = await _walletService.CreditAsync(creditRequest, ct);

            if (!creditResult.IsSuccess)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                _logger.LogError("Wallet credit failed during payment approval for payment {PaymentNumber}: {Message}",
                    payment.PaymentNumber, creditResult.Message);

                return AdminVerificationResult.BadRequest($"Wallet credit failed: {creditResult.Message}");
            }

            // 12. Persist payment changes and commit transaction atomically
            await _db.SaveChangesAsync(ct);

            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("Top-up {PaymentNumber} approved by Admin {AdminId}. Credited {Amount} PKR to Wallet {WalletId}. New balance: {Balance}",
                payment.PaymentNumber, adminUserId, payment.Amount, payment.Wallet.Id, creditResult.Wallet!.Balance);

            var adminDto = MapToAdminDto(payment);
            var resultDto = new ApproveManualPaymentResultDto
            {
                Payment = adminDto,
                CreditedAmount = payment.Amount,
                WalletNewBalance = creditResult.Wallet.Balance,
                WalletOperationKey = operationKey
            };

            return AdminVerificationResult.Approved(resultDto);
        }
        catch (DbUpdateException ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);

            if (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                if (pgEx.ConstraintName != null &&
                    (pgEx.ConstraintName.Contains("IX_Payments_Paid_Account_ProviderTransactionId") ||
                     pgEx.ConstraintName.Contains("IX_Payments_Paid_ProviderTransactionId")))
                {
                    return AdminVerificationResult.Conflict($"External transaction reference '{normalizedTrxId}' in receiving account '{normalizedAccount}' has already been used to approve another payment.");
                }
            }

            _logger.LogError(ex, "DbUpdateException while approving top-up {PaymentNumber}", paymentNumber);
            throw;
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Unexpected error while approving top-up {PaymentNumber}", paymentNumber);
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

    public async Task<AdminVerificationResult> RejectManualTopUpAsync(
        string adminUserId,
        string paymentNumber,
        RejectManualPaymentRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return AdminVerificationResult.BadRequest("Admin user ID is required.");
        }

        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            return AdminVerificationResult.BadRequest("PaymentNumber is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return AdminVerificationResult.BadRequest("Rejection reason is required.");
        }

        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational())
        {
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        }

        try
        {
            Payment? payment;
            if (_db.Database.IsRelational())
            {
                payment = await _db.Payments
                    .FromSqlInterpolated($"SELECT * FROM \"Payments\" WHERE \"PaymentNumber\" = {paymentNumber.Trim()} AND \"Purpose\" = 'WalletTopUp' FOR UPDATE")
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                payment = await _db.Payments
                    .FirstOrDefaultAsync(p => p.PaymentNumber == paymentNumber.Trim() && p.Purpose == PaymentPurpose.WalletTopUp, ct);
            }

            if (payment == null)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.NotFound($"Top-up payment '{paymentNumber}' was not found.");
            }

            await _db.Entry(payment).Reference(p => p.Wallet).LoadAsync(ct);
            await _db.Entry(payment).Collection(p => p.StatusHistory).LoadAsync(ct);

            if (payment.Status == PaymentStatus.Failed)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.Conflict($"Payment '{paymentNumber}' has already been rejected.");
            }

            if (payment.Status != PaymentStatus.Pending)
            {
                if (tx != null) await tx.RollbackAsync(ct);
                return AdminVerificationResult.BadRequest($"Cannot reject payment in '{payment.Status}' status. Only Pending payments can be rejected.");
            }

            payment.Status = PaymentStatus.Failed;
            payment.VerifiedByUserId = adminUserId;
            payment.AdminNotes = request.AdminNotes?.Trim();
            payment.VerifiedAt = DateTime.UtcNow;
            payment.UpdatedAt = DateTime.UtcNow;

            var historyEntry = new PaymentStatusHistory
            {
                PaymentId = payment.Id,
                PreviousStatus = PaymentStatus.Pending,
                NewStatus = PaymentStatus.Failed,
                Reason = $"Rejected by administrator: {request.Reason.Trim()}",
                ChangedByUserId = adminUserId,
                ChangedAt = DateTime.UtcNow
            };
            payment.StatusHistory.Add(historyEntry);

            // Balance is NOT touched.

            await _db.SaveChangesAsync(ct);

            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("Top-up {PaymentNumber} rejected by Admin {AdminId}. Reason: {Reason}",
                payment.PaymentNumber, adminUserId, request.Reason);

            return AdminVerificationResult.Rejected(MapToAdminDto(payment));
        }
        catch (Exception ex)
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Unexpected error while rejecting top-up {PaymentNumber}", paymentNumber);
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

    private static AdminManualPaymentDto MapToAdminDto(Payment payment)
    {
        return new AdminManualPaymentDto
        {
            Id = payment.Id,
            PaymentNumber = payment.PaymentNumber,
            Purpose = payment.Purpose.ToString(),
            WalletId = payment.WalletId,
            OrderId = payment.OrderId,
            CustomerId = payment.Wallet?.CustomerId ?? string.Empty,
            CustomerEmail = payment.Wallet?.Customer?.Email,
            CustomerFullName = payment.Wallet?.Customer?.FullName,
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentMethodCode = payment.PaymentMethodCode,
            Status = payment.Status.ToString(),
            IdempotencyKey = payment.IdempotencyKey,
            ProviderTransactionId = payment.ProviderTransactionId,
            CustomerReference = payment.CustomerReference,
            CustomerTransferDate = payment.CustomerTransferDate,
            CustomerSenderAccount = payment.CustomerSenderAccount,
            CustomerNotes = payment.CustomerNotes,
            CustomerProofSubmittedAt = payment.CustomerProofSubmittedAt,
            VerifiedByUserId = payment.VerifiedByUserId,
            VerifiedByUserName = payment.VerifiedByUser?.UserName,
            VerifiedReceivingAccount = payment.VerifiedReceivingAccount,
            AdminNotes = payment.AdminNotes,
            VerifiedAt = payment.VerifiedAt,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = payment.UpdatedAt,
            PaidAt = payment.PaidAt,
            StatusHistory = payment.StatusHistory
                .OrderBy(h => h.ChangedAt)
                .Select(h => new PaymentStatusHistoryDto
                {
                    Id = h.Id,
                    PreviousStatus = h.PreviousStatus?.ToString(),
                    NewStatus = h.NewStatus.ToString(),
                    Reason = h.Reason,
                    ChangedByUserId = h.ChangedByUserId,
                    ChangedByUserName = h.ChangedByUser?.UserName,
                    ChangedAt = h.ChangedAt
                })
                .ToList()
        };
    }

    private static readonly HashSet<string> AmbiguousReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "N/A", "NA", "NONE", "NULL", "TEST", "TESTING", "UNKNOWN", "NIL", "NOTAPPLICABLE",
        "0", "00", "000", "0000", "00000", "1234", "12345", "123456", "PAID", "RECEIVED", "CASH", "PENDING", "REF", "TXN"
    };

    private static bool IsUnreliableReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length < 4)
        {
            return true;
        }

        if (AmbiguousReferences.Contains(reference))
        {
            return true;
        }

        // Reject if all characters are identical (e.g. "0000", "1111", "AAAA", "----")
        if (reference.Distinct().Count() == 1)
        {
            return true;
        }

        return false;
    }
}
