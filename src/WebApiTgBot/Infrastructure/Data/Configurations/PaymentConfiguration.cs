using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for Payments.
/// Enforces unique PaymentNumber, decimal precision for monetary amounts,
/// foreign key to Order with Restrict delete behavior, and durable idempotency.
/// </summary>
public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(p => p.Id);

        // Unique payment reference constraint
        builder.Property(p => p.PaymentNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(p => p.PaymentNumber)
            .IsUnique()
            .HasDatabaseName("IX_Payments_PaymentNumber");

        builder.Property(p => p.OrderId)
            .IsRequired();

        // Monetary amount: explicit decimal(18,4) precision
        builder.Property(p => p.Amount)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(p => p.Currency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("PKR");

        builder.Property(p => p.PaymentMethodCode)
            .IsRequired()
            .HasMaxLength(50);

        // Status stored as string for transparency in PostgreSQL
        builder.Property(p => p.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(p => p.IdempotencyKey)
            .HasMaxLength(100);

        builder.Property(p => p.ProviderTransactionId)
            .HasMaxLength(150);

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .IsRequired();

        builder.Property(p => p.PaidAt);

        builder.Property(p => p.ExpiredAt);

        // FK: Order — Restrict delete so deleting an order with associated payment records is strictly prohibited
        builder.HasOne(p => p.Order)
            .WithMany(o => o.Payments)
            .HasForeignKey(p => p.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Enforce durable idempotency scoped to the order when key is provided
        builder.HasIndex(p => new { p.OrderId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_OrderId_IdempotencyKey");

        // Filtered index on ProviderTransactionId for fast lookups and webhook/reconciliation correlation
        builder.HasIndex(p => p.ProviderTransactionId)
            .HasFilter("\"ProviderTransactionId\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_ProviderTransactionId");

        // Performance indexes for querying payments by order status and lifecycle
        builder.HasIndex(p => new { p.OrderId, p.Status })
            .HasDatabaseName("IX_Payments_OrderId_Status");

        builder.HasIndex(p => new { p.Status, p.CreatedAt })
            .HasDatabaseName("IX_Payments_Status_CreatedAt");
    }
}
