using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Orders.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for OrderStatusHistory.
/// Enforces permanent audit records for every status transition.
/// </summary>
public class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("OrderStatusHistory");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.OrderId)
            .IsRequired();

        builder.Property(h => h.PreviousStatus)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(h => h.NewStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(h => h.Reason)
            .HasMaxLength(1000);

        builder.Property(h => h.ChangedByUserId)
            .HasMaxLength(450);

        builder.Property(h => h.ChangedAt)
            .IsRequired();

        // FK: Order — restrict delete so deleting an order containing audit history is rejected to preserve permanent audit records
        builder.HasOne(h => h.Order)
            .WithMany(o => o.StatusHistory)
            .HasForeignKey(h => h.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK: ChangedByUser (ApplicationUser) — SetNull if user is deleted to preserve history entry
        builder.HasOne(h => h.ChangedByUser)
            .WithMany()
            .HasForeignKey(h => h.ChangedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for chronological history retrieval for an order
        builder.HasIndex(h => new { h.OrderId, h.ChangedAt })
            .HasDatabaseName("IX_OrderStatusHistory_OrderId_ChangedAt");
    }
}
