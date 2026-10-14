using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

public class FulfillmentAttemptConfiguration : IEntityTypeConfiguration<FulfillmentAttempt>
{
    public void Configure(EntityTypeBuilder<FulfillmentAttempt> builder)
    {
        builder.ToTable("FulfillmentAttempts", t =>
        {
            t.HasCheckConstraint("CK_FulfillmentAttempts_AttemptNumber_Positive", "\"AttemptNumber\" > 0");
        });

        builder.HasKey(fa => fa.Id);

        builder.Property(fa => fa.AttemptNumber)
            .IsRequired();

        builder.Property(fa => fa.AttemptKey)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(fa => fa.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(fa => fa.ExternalVendorOrderId)
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(fa => fa.FailureCategory)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(fa => fa.SafeErrorSummary)
            .HasMaxLength(500)
            .IsRequired(false);

        builder.Property(fa => fa.CreatedAt)
            .IsRequired();

        builder.Property(fa => fa.UpdatedAt)
            .IsRequired();

        builder.Property(fa => fa.SubmittedAt)
            .IsRequired(false);

        builder.Property(fa => fa.CompletedAt)
            .IsRequired(false);

        builder.Property(fa => fa.InitiatedByUserId)
            .HasMaxLength(450)
            .IsRequired(false);

        builder.Property(fa => fa.ConfirmedByUserId)
            .HasMaxLength(450)
            .IsRequired(false);

        builder.Property(fa => fa.DeliveryReference)
            .HasMaxLength(100)
            .IsRequired(false);

        builder.Property(fa => fa.DeliveryChannel)
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(fa => fa.Notes)
            .HasMaxLength(500)
            .IsRequired(false);

        // Foreign key to Fulfillment
        builder.HasOne(fa => fa.Fulfillment)
            .WithMany(f => f.Attempts)
            .HasForeignKey(fa => fa.FulfillmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Foreign key to VendorProduct (nullable for manual fulfillment)
        builder.HasOne(fa => fa.VendorProduct)
            .WithMany(vp => vp.FulfillmentAttempts)
            .HasForeignKey(fa => fa.VendorProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Foreign keys to AspNetUsers for admin attribution
        builder.HasOne(fa => fa.InitiatedByUser)
            .WithMany()
            .HasForeignKey(fa => fa.InitiatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(fa => fa.ConfirmedByUser)
            .WithMany()
            .HasForeignKey(fa => fa.ConfirmedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique indexes
        builder.HasIndex(fa => fa.AttemptKey)
            .IsUnique();

        builder.HasIndex(fa => new { fa.FulfillmentId, fa.AttemptNumber })
            .IsUnique();

        builder.HasIndex(fa => fa.VendorProductId);
        builder.HasIndex(fa => fa.Status);
    }
}
