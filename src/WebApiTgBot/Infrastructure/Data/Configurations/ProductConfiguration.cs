using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Catalog.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for Products.
/// Enforces unique InternalSku, decimal precision for money, and FK to Category.
/// </summary>
public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.InternalSku)
            .IsRequired()
            .HasMaxLength(100);

        // InternalSku must be globally unique — it's our primary product identifier
        builder.HasIndex(p => p.InternalSku)
            .IsUnique()
            .HasDatabaseName("IX_Products_InternalSku");

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(300);

        builder.Property(p => p.Description)
            .HasMaxLength(5000);

        builder.Property(p => p.CategoryId)
            .IsRequired();

        // Money: use decimal(18,4) — never float or double.
        // 18 total digits, 4 decimal places.
        // Handles PKR amounts like 5000.0000 and small USDT fractions.
        builder.Property(p => p.SellingPrice)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(p => p.SellingCurrency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("PKR");

        builder.Property(p => p.IsActive)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .IsRequired();

        // FK: Category — a product belongs to one category.
        // Restrict delete: cannot delete a category that has products.
        builder.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index on CategoryId for fast category filtering in public API
        builder.HasIndex(p => p.CategoryId)
            .HasDatabaseName("IX_Products_CategoryId");

        // Composite index: fast lookups for active products in a category
        builder.HasIndex(p => new { p.CategoryId, p.IsActive })
            .HasDatabaseName("IX_Products_CategoryId_IsActive");
    }
}
