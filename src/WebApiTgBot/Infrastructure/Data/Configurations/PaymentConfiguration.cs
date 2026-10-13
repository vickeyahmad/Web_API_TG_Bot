using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for Payments.
/// Enforces unique PaymentNumber, decimal precision for monetary amounts,
/// foreign key to Order or Wallet with Restrict delete behavior, durable idempotency,
/// and check constraint ensuring payments target either an Order or a Wallet.
/// </summary>
public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", table =>
        {
            table.HasCheckConstraint(
                "CK_Payments_Purpose_Target",
                "(\"Purpose\" = 'OrderPurchase' AND \"OrderId\" IS NOT NULL AND \"WalletId\" IS NULL) OR (\"Purpose\" = 'WalletTopUp' AND \"WalletId\" IS NOT NULL AND \"OrderId\" IS NULL)");
            table.HasCheckConstraint(
                "CK_Payments_Amount_Positive",
                "\"Amount\" > 0");
        });

        builder.HasKey(p => p.Id);

        // Unique payment reference constraint
        builder.Property(p => p.PaymentNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(p => p.PaymentNumber)
            .IsUnique()
            .HasDatabaseName("IX_Payments_PaymentNumber");

        builder.Property(p => p.Purpose)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50)
            .HasDefaultValue(PaymentPurpose.OrderPurchase)
            .HasSentinel((PaymentPurpose)0);

        builder.Property(p => p.OrderId)
            .IsRequired(false);

        builder.Property(p => p.WalletId)
            .IsRequired(false);

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

        // Customer proof fields
        builder.Property(p => p.CustomerReference)
            .HasMaxLength(150);

        builder.Property(p => p.CustomerTransferDate);

        builder.Property(p => p.CustomerSenderAccount)
            .HasMaxLength(100);

        builder.Property(p => p.CustomerNotes)
            .HasMaxLength(1000);

        builder.Property(p => p.CustomerProofSubmittedAt);

        // Admin verification fields
        builder.Property(p => p.VerifiedByUserId)
            .HasMaxLength(450);

        builder.Property(p => p.VerifiedReceivingAccount)
            .HasMaxLength(100);

        builder.Property(p => p.AdminNotes)
            .HasMaxLength(1000);

        builder.Property(p => p.VerifiedAt);

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

        // FK: Wallet — Restrict delete so deleting a wallet with associated top-up records is strictly prohibited
        builder.HasOne(p => p.Wallet)
            .WithMany(w => w.TopUpPayments)
            .HasForeignKey(p => p.WalletId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK: VerifiedByUser (ApplicationUser) — SetNull if user is deleted to preserve financial audit integrity
        builder.HasOne(p => p.VerifiedByUser)
            .WithMany()
            .HasForeignKey(p => p.VerifiedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Enforce durable idempotency scoped to Order when key is provided
        builder.HasIndex(p => new { p.OrderId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"OrderId\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_OrderId_IdempotencyKey");

        // Enforce durable idempotency scoped to Wallet when key is provided
        builder.HasIndex(p => new { p.WalletId, p.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"WalletId\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_WalletId_IdempotencyKey");

        // General index on ProviderTransactionId for fast lookups and webhook/reconciliation correlation
        builder.HasIndex(p => p.ProviderTransactionId)
            .HasFilter("\"ProviderTransactionId\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_ProviderTransactionId");

        // Strict uniqueness on (VerifiedReceivingAccount, ProviderTransactionId) when Status is Paid to prevent duplicate funding from same bank transaction
        builder.HasIndex(p => new { p.VerifiedReceivingAccount, p.ProviderTransactionId })
            .IsUnique()
            .HasFilter("\"Status\" = 'Paid' AND \"VerifiedReceivingAccount\" IS NOT NULL AND \"ProviderTransactionId\" IS NOT NULL")
            .HasDatabaseName("IX_Payments_Paid_Account_ProviderTransactionId");

        // Performance indexes for querying payments by order status, wallet status, and lifecycle
        builder.HasIndex(p => new { p.OrderId, p.Status })
            .HasDatabaseName("IX_Payments_OrderId_Status");

        builder.HasIndex(p => new { p.WalletId, p.Status })
            .HasDatabaseName("IX_Payments_WalletId_Status");

        builder.HasIndex(p => new { p.Status, p.CreatedAt })
            .HasDatabaseName("IX_Payments_Status_CreatedAt");
    }
}
