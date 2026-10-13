using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Payments.Models.DTOs;
using WebApiTgBot.Modules.Payments.Options;
using WebApiTgBot.Modules.Wallets.Services;

namespace WebApiTgBot.Modules.Payments.Services;

/// <summary>
/// Business service implementing customer-initiated manual wallet top-up requests and transfer proof submission.
/// Enforces feature gate check, min/max limits, customer account validity, and durable idempotency.
/// Never mutates wallet balance directly; balance changes require administrator verification.
/// </summary>
public class CustomerWalletTopUpService : ICustomerWalletTopUpService
{
    private readonly AppDbContext _db;
    private readonly IWalletService _walletService;
    private readonly IPaymentNumberGenerator _paymentNumberGenerator;
    private readonly WalletTopUpOptions _options;
    private readonly ILogger<CustomerWalletTopUpService> _logger;

    public CustomerWalletTopUpService(
        AppDbContext db,
        IWalletService walletService,
        IPaymentNumberGenerator paymentNumberGenerator,
        IOptions<WalletTopUpOptions> options,
        ILogger<CustomerWalletTopUpService> logger)
    {
        _db = db;
        _walletService = walletService;
        _paymentNumberGenerator = paymentNumberGenerator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WalletTopUpOperationResult> CreateTopUpAsync(
        string customerId,
        CreateCustomerWalletTopUpRequest request,
        string? headerIdempotencyKey,
        CancellationToken ct = default)
    {
        // 1. Feature Gate Check
        if (!_options.Enabled)
        {
            return WalletTopUpOperationResult.FeatureDisabled("Manual wallet top-ups are currently disabled.");
        }

        if (string.IsNullOrWhiteSpace(customerId))
        {
            return WalletTopUpOperationResult.BadRequest("CustomerId is required.");
        }

        // 2. Customer Account Validation
        var customer = await _db.Users.FindAsync(new object[] { customerId }, ct);
        if (customer == null || !customer.IsActive)
        {
            return WalletTopUpOperationResult.BadRequest("Customer account does not exist or is inactive.");
        }

        // 3. Amount Validation
        if (request.Amount < _options.MinAmount)
        {
            return WalletTopUpOperationResult.BadRequest($"Minimum top-up amount is {_options.MinAmount} {_options.Currency}.");
        }

        if (request.Amount > _options.MaxAmount)
        {
            return WalletTopUpOperationResult.BadRequest($"Maximum top-up amount is {_options.MaxAmount} {_options.Currency}.");
        }

        // 4. Payment Method Validation
        if (string.IsNullOrWhiteSpace(request.PaymentMethodCode))
        {
            return WalletTopUpOperationResult.BadRequest("PaymentMethodCode is required.");
        }

        var normalizedMethod = request.PaymentMethodCode.Trim().ToUpperInvariant();
        if (_options.AllowedMethodCodes == null || !_options.AllowedMethodCodes.Contains(normalizedMethod, StringComparer.OrdinalIgnoreCase))
        {
            return WalletTopUpOperationResult.BadRequest($"Payment method '{request.PaymentMethodCode}' is not supported for manual top-ups.");
        }

        // 5. Ensure Customer's PKR Wallet Exists
        var wallet = await _walletService.GetOrCreateWalletAsync(customerId, _options.Currency, ct);

        // 6. Resolve Idempotency Key
        var idempotencyKey = !string.IsNullOrWhiteSpace(headerIdempotencyKey)
            ? headerIdempotencyKey.Trim()
            : request.IdempotencyKey?.Trim();

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existing = await _db.Payments
                .Include(p => p.Wallet)
                .FirstOrDefaultAsync(p => p.WalletId == wallet.Id && p.IdempotencyKey == idempotencyKey, ct);

            if (existing != null)
            {
                if (existing.Amount == request.Amount &&
                    string.Equals(existing.PaymentMethodCode, normalizedMethod, StringComparison.OrdinalIgnoreCase))
                {
                    return WalletTopUpOperationResult.Succeeded(MapToDto(existing), "Existing top-up returned.", isExisting: true);
                }

                return WalletTopUpOperationResult.Conflict("Idempotency key was already used for a different top-up request.");
            }
        }

        // 7. Generate collision-resistant unique PaymentNumber
        var paymentNumber = _paymentNumberGenerator.GeneratePaymentNumber();

        // 8. Create Payment record in Pending status
        var payment = new Payment
        {
            PaymentNumber = paymentNumber,
            Purpose = PaymentPurpose.WalletTopUp,
            WalletId = wallet.Id,
            OrderId = null,
            Amount = request.Amount,
            Currency = _options.Currency,
            PaymentMethodCode = normalizedMethod,
            Status = PaymentStatus.Pending,
            IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
            CustomerNotes = request.Notes?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // 9. Record initial audit trail entry
        var initialHistory = new PaymentStatusHistory
        {
            Payment = payment,
            PreviousStatus = null,
            NewStatus = PaymentStatus.Pending,
            Reason = "Manual PKR wallet top-up initiated by customer.",
            ChangedByUserId = customerId,
            ChangedAt = DateTime.UtcNow
        };

        payment.StatusHistory.Add(initialHistory);
        _db.Payments.Add(payment);

        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Top-up {PaymentNumber} initiated for Customer {CustomerId}, Amount: {Amount} PKR",
                paymentNumber, customerId, request.Amount);

            return WalletTopUpOperationResult.Succeeded(MapToDto(payment), "Top-up initiated successfully. Please transfer funds and submit proof.", isExisting: false);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "DbUpdateException during top-up creation for Customer {CustomerId}", customerId);

            if (!string.IsNullOrEmpty(idempotencyKey) && ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
            {
                _db.ChangeTracker.Clear();
                var existing = await _db.Payments
                    .Include(p => p.Wallet)
                    .FirstOrDefaultAsync(p => p.WalletId == wallet.Id && p.IdempotencyKey == idempotencyKey, ct);

                if (existing != null)
                {
                    if (existing.Amount == request.Amount &&
                        string.Equals(existing.PaymentMethodCode, normalizedMethod, StringComparison.OrdinalIgnoreCase))
                    {
                        return WalletTopUpOperationResult.Succeeded(MapToDto(existing), "Existing top-up returned.", isExisting: true);
                    }

                    return WalletTopUpOperationResult.Conflict("Idempotency key was already used for a different top-up request.");
                }
            }

            throw;
        }
    }

    public async Task<WalletTopUpOperationResult> SubmitProofAsync(
        string customerId,
        string paymentNumber,
        SubmitTransferProofRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return WalletTopUpOperationResult.BadRequest("CustomerId is required.");
        }

        if (string.IsNullOrWhiteSpace(paymentNumber))
        {
            return WalletTopUpOperationResult.BadRequest("PaymentNumber is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CustomerReference))
        {
            return WalletTopUpOperationResult.BadRequest("CustomerReference is required.");
        }

        var payment = await _db.Payments
            .Include(p => p.Wallet)
            .Include(p => p.StatusHistory)
            .FirstOrDefaultAsync(p => p.PaymentNumber == paymentNumber.Trim() &&
                                      p.Purpose == PaymentPurpose.WalletTopUp &&
                                      p.Wallet != null &&
                                      p.Wallet.CustomerId == customerId, ct);

        if (payment == null)
        {
            return WalletTopUpOperationResult.NotFound($"Top-up payment '{paymentNumber}' was not found.");
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return WalletTopUpOperationResult.BadRequest($"Cannot submit proof for payment in '{payment.Status}' status. Proof can only be submitted for Pending payments.");
        }

        payment.CustomerReference = request.CustomerReference.Trim();
        payment.CustomerTransferDate = request.CustomerTransferDate ?? DateTime.UtcNow;
        payment.CustomerSenderAccount = request.CustomerSenderAccount?.Trim();
        if (!string.IsNullOrWhiteSpace(request.CustomerNotes))
        {
            payment.CustomerNotes = request.CustomerNotes.Trim();
        }
        payment.CustomerProofSubmittedAt = DateTime.UtcNow;
        payment.UpdatedAt = DateTime.UtcNow;

        var historyNote = new PaymentStatusHistory
        {
            PaymentId = payment.Id,
            PreviousStatus = PaymentStatus.Pending,
            NewStatus = PaymentStatus.Pending,
            Reason = $"Customer submitted transfer proof (Ref: {payment.CustomerReference}).",
            ChangedByUserId = customerId,
            ChangedAt = DateTime.UtcNow
        };

        _db.PaymentStatusHistories.Add(historyNote);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Transfer proof submitted for payment {PaymentNumber} by customer {CustomerId}. Ref: {Ref}",
            payment.PaymentNumber, customerId, payment.CustomerReference);

        return WalletTopUpOperationResult.Succeeded(MapToDto(payment), "Proof of transfer submitted successfully. Pending administrator verification.");
    }

    public async Task<PagedResult<CustomerWalletTopUpDto>> GetCustomerTopUpsAsync(
        string customerId,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.Payments
            .AsNoTracking()
            .Where(p => p.Purpose == PaymentPurpose.WalletTopUp &&
                        p.Wallet != null &&
                        p.Wallet.CustomerId == customerId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => MapToDto(p, _options))
            .ToListAsync(ct);

        return new PagedResult<CustomerWalletTopUpDto>(items, page, pageSize, total);
    }

    public async Task<CustomerWalletTopUpDto?> GetCustomerTopUpByNumberAsync(
        string customerId,
        string paymentNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(paymentNumber))
        {
            return null;
        }

        var payment = await _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PaymentNumber == paymentNumber.Trim() &&
                                      p.Purpose == PaymentPurpose.WalletTopUp &&
                                      p.Wallet != null &&
                                      p.Wallet.CustomerId == customerId, ct);

        return payment == null ? null : MapToDto(payment);
    }

    private CustomerWalletTopUpDto MapToDto(Payment payment) =>
        MapToDto(payment, _options);

    private static CustomerWalletTopUpDto MapToDto(Payment payment, WalletTopUpOptions options) =>
        new()
        {
            PaymentNumber = payment.PaymentNumber,
            Amount = payment.Amount,
            Currency = payment.Currency,
            PaymentMethodCode = payment.PaymentMethodCode,
            Status = payment.Status.ToString(),
            CreatedAt = payment.CreatedAt,
            PaidAt = payment.PaidAt,
            CustomerProofSubmittedAt = payment.CustomerProofSubmittedAt,
            CustomerReference = payment.CustomerReference,
            CustomerTransferDate = payment.CustomerTransferDate,
            CustomerSenderAccount = payment.CustomerSenderAccount,
            CustomerNotes = payment.CustomerNotes,
            BankName = options.BankName,
            AccountTitle = options.AccountTitle,
            AccountNumber = options.AccountNumber,
            Instructions = options.Instructions
        };
}
