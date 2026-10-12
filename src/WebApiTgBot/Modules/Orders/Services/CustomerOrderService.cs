using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebApiTgBot.Common.Models;
using WebApiTgBot.Infrastructure.Data;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Orders.Models.DTOs;

namespace WebApiTgBot.Modules.Orders.Services;

/// <summary>
/// Service implementing customer-facing order placement and tracking.
/// Guarantees atomic transaction execution, server-side price resolution,
/// durable idempotency, and strict customer ownership isolation.
/// </summary>
public class CustomerOrderService : ICustomerOrderService
{
    private readonly AppDbContext _dbContext;
    private readonly IOrderNumberGenerator _orderNumberGenerator;
    private readonly ILogger<CustomerOrderService> _logger;

    public CustomerOrderService(
        AppDbContext dbContext,
        IOrderNumberGenerator orderNumberGenerator,
        ILogger<CustomerOrderService> logger)
    {
        _dbContext = dbContext;
        _orderNumberGenerator = orderNumberGenerator;
        _logger = logger;
    }

    public async Task<CustomerOrderResult> CreateOrderAsync(
        string customerId,
        CreateCustomerOrderRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return CustomerOrderResult.Forbidden("Unauthenticated customer request.");
        }

        // 1. Resolve and validate client idempotency key
        var effectiveKey = !string.IsNullOrWhiteSpace(idempotencyKey)
            ? idempotencyKey.Trim()
            : (!string.IsNullOrWhiteSpace(request.IdempotencyKey) ? request.IdempotencyKey.Trim() : null);

        if (string.IsNullOrWhiteSpace(effectiveKey))
        {
            return CustomerOrderResult.Invalid("An idempotency key is required via X-Idempotency-Key header or request body.");
        }

        if (effectiveKey.Length > 100)
        {
            return CustomerOrderResult.Invalid("Idempotency key cannot exceed 100 characters.");
        }

        // 2. Sequential Idempotency Check: inspect if this customer already submitted this key
        var existingOrder = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == effectiveKey, cancellationToken);

        if (existingOrder != null)
        {
            var existingItem = existingOrder.Items.FirstOrDefault();
            if (existingItem != null && string.Equals(existingItem.ProductSkuSnapshot, request.ProductSku.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Idempotent order request recognized for Customer {CustomerId}, returning existing Order {OrderNumber}",
                    customerId, existingOrder.OrderNumber);
                return CustomerOrderResult.Ok(MapToDetailDto(existingOrder), "Order retrieved from existing idempotent submission.", isExistingIdempotent: true);
            }

            _logger.LogWarning("Idempotency conflict for Customer {CustomerId}: key '{Key}' reused with different SKU {NewSku}",
                customerId, effectiveKey, request.ProductSku);
            return CustomerOrderResult.Conflict("The provided idempotency key has already been used with different order details.");
        }

        // 3. Catalog Validation: retrieve product directly from database
        var normalizedSku = request.ProductSku.Trim();
        var product = await _dbContext.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.InternalSku == normalizedSku, cancellationToken);

        if (product == null)
        {
            _logger.LogWarning("Order creation rejected: Product SKU '{Sku}' not found", normalizedSku);
            return CustomerOrderResult.NotFound($"Product with SKU '{normalizedSku}' was not found.");
        }

        if (!product.IsActive)
        {
            _logger.LogWarning("Order creation rejected: Product '{Sku}' is inactive", normalizedSku);
            return CustomerOrderResult.Invalid($"Product '{normalizedSku}' is currently inactive or unavailable.");
        }

        if (product.Category == null || !product.Category.IsActive)
        {
            _logger.LogWarning("Order creation rejected: Category for product '{Sku}' is inactive", normalizedSku);
            return CustomerOrderResult.Invalid($"The category for product '{normalizedSku}' is currently inactive or unavailable.");
        }

        if (!string.Equals(product.SellingCurrency, "PKR", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Order creation rejected: Unsupported checkout currency '{Currency}' for SKU '{Sku}'",
                product.SellingCurrency, normalizedSku);
            return CustomerOrderResult.Invalid($"Unsupported checkout currency '{product.SellingCurrency}'. Only PKR-priced products are supported.");
        }

        if (product.SellingPrice <= 0)
        {
            _logger.LogWarning("Order creation rejected: Product '{Sku}' has non-positive price {Price}", normalizedSku, product.SellingPrice);
            return CustomerOrderResult.Invalid("Invalid product pricing. Zero-value transactions are not supported.");
        }

        // 4. Bounded unique order number generation with collision retry
        const int maxOrderNumberAttempts = 3;
        string orderNumber = string.Empty;
        for (int i = 0; i < maxOrderNumberAttempts; i++)
        {
            var candidate = _orderNumberGenerator.GenerateOrderNumber();
            if (!await _dbContext.Orders.AnyAsync(o => o.OrderNumber == candidate, cancellationToken))
            {
                orderNumber = candidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(orderNumber))
        {
            _logger.LogError("Failed to allocate unique order number after {Attempts} attempts", maxOrderNumberAttempts);
            return CustomerOrderResult.Conflict("Failed to allocate a unique order number. Please try again.");
        }

        // 5. Construct Order, OrderItem snapshot, and initial OrderStatusHistory
        var order = new Order
        {
            OrderNumber = orderNumber,
            CustomerId = customerId,
            Status = OrderStatus.PendingPayment,
            Currency = product.SellingCurrency,
            Subtotal = product.SellingPrice,
            TotalAmount = product.SellingPrice,
            SalesChannel = SalesChannel.Website,
            CustomerNotes = string.IsNullOrWhiteSpace(request.CustomerNotes) ? null : request.CustomerNotes.Trim(),
            IdempotencyKey = effectiveKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var orderItem = new OrderItem
        {
            Order = order,
            ProductId = product.Id,
            ProductSkuSnapshot = product.InternalSku,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = product.SellingPrice,
            Currency = product.SellingCurrency,
            TotalPrice = product.SellingPrice
        };
        order.Items.Add(orderItem);

        var history = new OrderStatusHistory
        {
            Order = order,
            PreviousStatus = null,
            NewStatus = OrderStatus.PendingPayment,
            Reason = "Order created via website checkout",
            ChangedByUserId = customerId,
            ChangedAt = DateTime.UtcNow
        };
        order.StatusHistory.Add(history);

        // 6. Save atomically in a database transaction
        try
        {
            IDbContextTransaction? transaction = null;
            if (_dbContext.Database.IsRelational())
            {
                transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                _dbContext.Orders.Add(order);
                await _dbContext.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }

            _logger.LogInformation("Order {OrderNumber} created successfully for Customer {CustomerId}", order.OrderNumber, customerId);
            return CustomerOrderResult.Ok(MapToDetailDto(order), "Order created successfully.");
        }
        catch (DbUpdateException ex)
        {
            var pgEx = ex.InnerException as PostgresException ?? ex.GetBaseException() as PostgresException;

            // Concurrent race collision handling on (CustomerId, IdempotencyKey)
            if (pgEx != null && pgEx.SqlState == "23505" &&
                (pgEx.ConstraintName?.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase) == true ||
                 pgEx.Message.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase) ||
                 pgEx.Detail?.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase) == true))
            {
                _logger.LogWarning("Concurrent idempotent collision caught for Customer {CustomerId}, key {Key}", customerId, effectiveKey);

                // Clear change tracker to discard failed in-memory entities before querying
                _dbContext.ChangeTracker.Clear();

                // Retrieve the committed winning order with a bounded retry in case of microsecond commit lag
                Order? winningOrder = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    winningOrder = await _dbContext.Orders
                        .AsNoTracking()
                        .Include(o => o.Items)
                        .Include(o => o.StatusHistory)
                        .FirstOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == effectiveKey, cancellationToken);

                    if (winningOrder != null)
                    {
                        break;
                    }

                    await Task.Delay(50, cancellationToken);
                }

                if (winningOrder != null)
                {
                    var winningItem = winningOrder.Items.FirstOrDefault();
                    if (winningItem != null && string.Equals(winningItem.ProductSkuSnapshot, request.ProductSku.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Concurrent collision resolved: returning winning Order {OrderNumber} for Customer {CustomerId}",
                            winningOrder.OrderNumber, customerId);
                        return CustomerOrderResult.Ok(MapToDetailDto(winningOrder), "Order retrieved from existing idempotent submission.", isExistingIdempotent: true);
                    }

                    return CustomerOrderResult.Conflict("The provided idempotency key has already been used with different order details.");
                }

                return CustomerOrderResult.Conflict("An order with this idempotency key is currently being processed. Please retry shortly.");
            }

            throw;
        }
    }

    public async Task<PagedResult<CustomerOrderSummaryDto>> GetCustomerOrdersAsync(
        string customerId,
        CustomerOrderQuery query,
        CancellationToken cancellationToken = default)
    {
        var baseQuery = _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.CustomerId == customerId);

        if (query.Status.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.Status == query.Status.Value);
        }

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var orders = await baseQuery
            .OrderByDescending(o => o.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Include(o => o.Items)
            .ToListAsync(cancellationToken);

        var dtos = orders.Select(o => new CustomerOrderSummaryDto
        {
            OrderNumber = o.OrderNumber,
            Status = o.Status.ToString(),
            Currency = o.Currency,
            Subtotal = o.Subtotal,
            TotalAmount = o.TotalAmount,
            ItemCount = o.Items.Count,
            CreatedAt = o.CreatedAt,
            UpdatedAt = o.UpdatedAt
        }).ToList();

        return new PagedResult<CustomerOrderSummaryDto>(dtos, query.Page, query.PageSize, totalCount);
    }

    public async Task<CustomerOrderDetailDto?> GetCustomerOrderDetailAsync(
        string customerId,
        string orderNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return null;
        }

        var order = await _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber.Trim() && o.CustomerId == customerId, cancellationToken);

        return order == null ? null : MapToDetailDto(order);
    }

    private static CustomerOrderDetailDto MapToDetailDto(Order order)
    {
        return new CustomerOrderDetailDto
        {
            OrderNumber = order.OrderNumber,
            Status = order.Status.ToString(),
            Currency = order.Currency,
            Subtotal = order.Subtotal,
            TotalAmount = order.TotalAmount,
            SalesChannel = order.SalesChannel.ToString(),
            CustomerNotes = order.CustomerNotes,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Items = order.Items.Select(i => new CustomerOrderItemDto
            {
                Id = i.Id,
                ProductSku = i.ProductSkuSnapshot,
                ProductName = i.ProductNameSnapshot,
                UnitPrice = i.UnitPriceSnapshot,
                Quantity = i.Quantity,
                TotalPrice = i.TotalPrice,
                Currency = i.Currency
            }).ToList(),
            StatusHistory = order.StatusHistory
                .OrderBy(h => h.ChangedAt)
                .Select(h => new CustomerOrderStatusHistoryDto
                {
                    Status = h.NewStatus.ToString(),
                    Reason = h.Reason,
                    ChangedAt = h.ChangedAt
                }).ToList()
        };
    }
}
