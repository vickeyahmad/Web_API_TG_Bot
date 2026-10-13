using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for the permanent WalletTransactions ledger table.
/// Enforces positive transaction amount, durable idempotency per wallet,
/// exact decimal(18,4) precision, and permanent historical preservation via Restrict.
/// </summary>
public class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.ToTable("WalletTransactions", table =>
        {
            table.HasCheckConstraint("CK_WalletTransactions_Amount_Positive", "\"Amount\" > 0");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.WalletId)
            .IsRequired();

        builder.Property(t => t.TransactionType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(t => t.Amount)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(t => t.Currency)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(t => t.BalanceBefore)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(t => t.BalanceAfter)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(t => t.OperationKey)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(t => t.SourceType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(t => t.SourceReferenceId)
            .HasMaxLength(100);

        builder.Property(t => t.Description)
            .HasMaxLength(500);

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.CreatedByUserId)
            .HasMaxLength(450);

        // Durable idempotency constraint scoped to the wallet
        builder.HasIndex(t => new { t.WalletId, t.OperationKey })
            .IsUnique()
            .HasDatabaseName("IX_WalletTransactions_WalletId_OperationKey");

        // Index for chronological history retrieval and reconciliation
        builder.HasIndex(t => new { t.WalletId, t.CreatedAt })
            .HasDatabaseName("IX_WalletTransactions_WalletId_CreatedAt");

        // Filtered index on SourceReferenceId for tracing orders or external references
        builder.HasIndex(t => t.SourceReferenceId)
            .HasFilter("\"SourceReferenceId\" IS NOT NULL")
            .HasDatabaseName("IX_WalletTransactions_SourceReferenceId");

        // FK: Wallet — Restrict delete to guarantee permanent ledger retention
        builder.HasOne(t => t.Wallet)
            .WithMany(w => w.Transactions)
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK: CreatedByUser — SetNull if user record is removed in the future
        builder.HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
