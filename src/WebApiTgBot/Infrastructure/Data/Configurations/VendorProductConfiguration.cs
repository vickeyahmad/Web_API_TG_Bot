using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Catalog.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for VendorProducts (vendor-to-product mappings).
///
/// Security note: VendorCost and ExternalProductId are stored in this table.
/// They must NEVER appear in public API responses.
/// The service layer enforces this by using separate public DTOs.
/// </summary>
public class VendorProductConfiguration : IEntityTypeConfiguration<VendorProduct>
{
    public void Configure(EntityTypeBuilder<VendorProduct> builder)
    {
        builder.ToTable("VendorProducts");

        builder.HasKey(vp => vp.Id);

        builder.Property(vp => vp.VendorId)
            .IsRequired();

        builder.Property(vp => vp.ProductId)
            .IsRequired();

        builder.Property(vp => vp.ExternalProductId)
            .IsRequired()
            .HasMaxLength(200);

        // VendorCost: same precision rules as SellingPrice — decimal(18,4), never float.
        builder.Property(vp => vp.VendorCost)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(vp => vp.VendorCostCurrency)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(vp => vp.IsAvailable)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(vp => vp.Priority)
            .IsRequired()
            .HasDefaultValue(10);

        builder.Property(vp => vp.CreatedAt)
            .IsRequired();

        builder.Property(vp => vp.UpdatedAt)
            .IsRequired();

        // FK: Vendor — cascade delete removes mappings when vendor is deleted
        builder.HasOne(vp => vp.Vendor)
            .WithMany(v => v.VendorProducts)
            .HasForeignKey(vp => vp.VendorId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK: Product — cascade delete removes mappings when product is deleted
        builder.HasOne(vp => vp.Product)
            .WithMany(p => p.VendorProducts)
            .HasForeignKey(vp => vp.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevent duplicate mappings: a vendor cannot be mapped to the same product twice.
        builder.HasIndex(vp => new { vp.VendorId, vp.ProductId })
            .IsUnique()
            .HasDatabaseName("IX_VendorProducts_VendorId_ProductId");

        // Fast lookup: find available vendors for a product, sorted by priority.
        builder.HasIndex(vp => new { vp.ProductId, vp.IsAvailable, vp.Priority })
            .HasDatabaseName("IX_VendorProducts_ProductId_IsAvailable_Priority");
    }
}
