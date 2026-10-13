using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Payments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core table configuration for PaymentStatusHistory.
/// Enforces permanent audit records for every payment status transition.
/// Prohibits destructive cascading deletion of financial history.
/// </summary>
public class PaymentStatusHistoryConfiguration : IEntityTypeConfiguration<PaymentStatusHistory>
{
    public void Configure(EntityTypeBuilder<PaymentStatusHistory> builder)
    {
        builder.ToTable("PaymentStatusHistory");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.PaymentId)
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

        // FK: Payment — restrict delete so deleting a payment containing audit history is rejected
        builder.HasOne(h => h.Payment)
            .WithMany(p => p.StatusHistory)
            .HasForeignKey(h => h.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        // FK: ChangedByUser (ApplicationUser) — SetNull if user is deleted to preserve history entry
        builder.HasOne(h => h.ChangedByUser)
            .WithMany()
            .HasForeignKey(h => h.ChangedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for chronological history retrieval for a payment
        builder.HasIndex(h => new { h.PaymentId, h.ChangedAt })
            .HasDatabaseName("IX_PaymentStatusHistory_PaymentId_ChangedAt");
    }
}
