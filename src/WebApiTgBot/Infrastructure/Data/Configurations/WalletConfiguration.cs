using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for the Wallets table.
/// Enforces unique (CustomerId, Currency) constraint, non-negative balance check,
/// exact decimal(18,4) precision, and non-destructive Restrict delete behavior.
/// </summary>
public class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("Wallets", table =>
        {
            table.HasCheckConstraint("CK_Wallets_Balance_NonNegative", "\"Balance\" >= 0");
        });

        builder.HasKey(w => w.Id);

        builder.Property(w => w.CustomerId)
            .IsRequired()
            .HasMaxLength(450);

        builder.Property(w => w.Currency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("PKR");

        builder.Property(w => w.Balance)
            .IsRequired()
            .HasPrecision(18, 4)
            .HasDefaultValue(0.0000m);

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        builder.Property(w => w.UpdatedAt)
            .IsRequired();

        // Enforce exactly ONE wallet per customer per currency
        builder.HasIndex(w => new { w.CustomerId, w.Currency })
            .IsUnique()
            .HasDatabaseName("IX_Wallets_CustomerId_Currency");

        // FK: ApplicationUser — Restrict delete to prevent deleting customers who have financial wallet balances
        builder.HasOne(w => w.Customer)
            .WithMany()
            .HasForeignKey(w => w.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // One-to-many relationship with transactions: Restrict delete to ensure permanent financial audit preservation
        builder.HasMany(w => w.Transactions)
            .WithOne(t => t.Wallet)
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
