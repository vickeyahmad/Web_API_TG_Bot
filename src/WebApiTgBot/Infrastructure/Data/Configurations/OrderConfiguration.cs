using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for Orders.
/// Enforces unique OrderNumber, decimal precision for money amounts,
/// foreign key to ApplicationUser, and performance indexes.
/// </summary>
public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(o => o.Id);

        // Unique order number constraint
        builder.Property(o => o.OrderNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(o => o.OrderNumber)
            .IsUnique()
            .HasDatabaseName("IX_Orders_OrderNumber");

        builder.Property(o => o.CustomerId)
            .IsRequired()
            .HasMaxLength(450);

        // Status stored as string for transparency in PostgreSQL
        builder.Property(o => o.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(o => o.Currency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("PKR");

        // Money: explicit precision decimal(18,4)
        builder.Property(o => o.Subtotal)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(o => o.TotalAmount)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(o => o.SalesChannel)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(o => o.CustomerNotes)
            .HasMaxLength(2000);

        builder.Property(o => o.CreatedAt)
            .IsRequired();

        builder.Property(o => o.UpdatedAt)
            .IsRequired();

        // FK: Customer (ApplicationUser) — Restrict delete to preserve order history
        builder.HasOne(o => o.Customer)
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Performance indexes for querying orders by customer status and lifecycle creation
        builder.HasIndex(o => new { o.CustomerId, o.Status })
            .HasDatabaseName("IX_Orders_CustomerId_Status");

        builder.HasIndex(o => new { o.Status, o.CreatedAt })
            .HasDatabaseName("IX_Orders_Status_CreatedAt");
    }
}
