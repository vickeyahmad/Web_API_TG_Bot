using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

public class FulfillmentStatusHistoryConfiguration : IEntityTypeConfiguration<FulfillmentStatusHistory>
{
    public void Configure(EntityTypeBuilder<FulfillmentStatusHistory> builder)
    {
        builder.ToTable("FulfillmentStatusHistories");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.PreviousStatus)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired(false);

        builder.Property(h => h.NewStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(h => h.Reason)
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(h => h.ChangedByUserId)
            .HasMaxLength(450)
            .IsRequired(false);

        builder.Property(h => h.ChangedAt)
            .IsRequired();

        // Foreign key to Fulfillment (non-destructive Restrict delete)
        builder.HasOne(h => h.Fulfillment)
            .WithMany(f => f.StatusHistory)
            .HasForeignKey(h => h.FulfillmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Foreign key to AspNetUsers for admin attribution (SetNull if user deleted)
        builder.HasOne(h => h.ChangedByUser)
            .WithMany()
            .HasForeignKey(h => h.ChangedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(h => h.FulfillmentId);
        builder.HasIndex(h => h.ChangedAt);
    }
}
