using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebApiTgBot.Modules.Fulfillments.Models;

namespace WebApiTgBot.Infrastructure.Data.Configurations;

public class FulfillmentConfiguration : IEntityTypeConfiguration<Fulfillment>
{
    public void Configure(EntityTypeBuilder<Fulfillment> builder)
    {
        builder.ToTable("Fulfillments", t =>
        {
            t.HasCheckConstraint("CK_Fulfillments_QuantityRequested_Positive", "\"QuantityRequested\" > 0");
            t.HasCheckConstraint("CK_Fulfillments_QuantityFulfilled_Valid", "\"QuantityFulfilled\" >= 0 AND \"QuantityFulfilled\" <= \"QuantityRequested\"");
        });

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(f => f.FulfillmentMethod)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(f => f.QuantityRequested)
            .IsRequired();

        builder.Property(f => f.QuantityFulfilled)
            .IsRequired();

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.Property(f => f.UpdatedAt)
            .IsRequired();

        builder.Property(f => f.CompletedAt)
            .IsRequired(false);

        // One-to-one between OrderItem and Fulfillment (one fulfillment per order item)
        builder.HasOne(f => f.OrderItem)
            .WithOne(oi => oi.Fulfillment)
            .HasForeignKey<Fulfillment>(f => f.OrderItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // Unique index on OrderItemId to prevent duplicate fulfillment records per line item
        builder.HasIndex(f => f.OrderItemId)
            .IsUnique();

        // Foreign key to Order
        builder.HasOne(f => f.Order)
            .WithMany(o => o.Fulfillments)
            .HasForeignKey(f => f.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => f.OrderId);
        builder.HasIndex(f => f.Status);
    }
}
