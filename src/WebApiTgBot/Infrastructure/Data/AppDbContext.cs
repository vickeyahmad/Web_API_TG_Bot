using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WebApiTgBot.Modules.Catalog.Models;
using WebApiTgBot.Modules.Fulfillments.Models;
using WebApiTgBot.Modules.Identity.Models;
using WebApiTgBot.Modules.Orders.Models;
using WebApiTgBot.Modules.Payments.Models;
using WebApiTgBot.Modules.Wallets.Models;

namespace WebApiTgBot.Infrastructure.Data;

/// <summary>
/// The central database context for the application.
/// Inherits from IdentityDbContext to provide central authentication and authorization
/// tables for both administrators and future customers.
/// Modules add their entities via DbSet properties and IEntityTypeConfiguration classes.
/// </summary>
public class AppDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorProduct> VendorProducts => Set<VendorProduct>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentStatusHistory> PaymentStatusHistories => Set<PaymentStatusHistory>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<Fulfillment> Fulfillments => Set<Fulfillment>();
    public DbSet<FulfillmentAttempt> FulfillmentAttempts => Set<FulfillmentAttempt>();


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly automatically.
        // This picks up CategoryConfiguration, ProductConfiguration, VendorConfiguration,
        // VendorProductConfiguration, and ApplicationUserConfiguration.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
