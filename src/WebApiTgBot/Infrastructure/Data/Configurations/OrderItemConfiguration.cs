using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for OrderItems.
/// Enforces immutable price/sku/name snapshots, decimal money precision,
/// and proper foreign key relationships.
/// </summary>
public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");

        builder.HasKey(oi => oi.Id);

        builder.Property(oi => oi.OrderId)
            .IsRequired();

        builder.Property(oi => oi.ProductId)
            .IsRequired();

        builder.Property(oi => oi.ProductSkuSnapshot)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(oi => oi.ProductNameSnapshot)
            .IsRequired()
            .HasMaxLength(300);

        builder.Property(oi => oi.Quantity)
            .IsRequired();

        // Monetary precision decimal(18,4)
        builder.Property(oi => oi.UnitPriceSnapshot)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(oi => oi.Currency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("PKR");

        builder.Property(oi => oi.TotalPrice)
            .IsRequired()
            .HasPrecision(18, 4);

        // FK: Order — restrict delete so deleting an order containing items is rejected to prevent accidental deletion
        builder.HasOne(oi => oi.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(oi => oi.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK: Product — restrict delete so historical products cannot be deleted if referenced in orders
        builder.HasOne(oi => oi.Product)
            .WithMany()
            .HasForeignKey(oi => oi.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes for lookups by order and product
        builder.HasIndex(oi => oi.OrderId)
            .HasDatabaseName("IX_OrderItems_OrderId");

        builder.HasIndex(oi => oi.ProductId)
            .HasDatabaseName("IX_OrderItems_ProductId");
    }
}
