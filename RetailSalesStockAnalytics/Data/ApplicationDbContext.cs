using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Models;

namespace RetailSalesStockAnalytics.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Category> Categories { get; set; }

        public DbSet<Product> Products { get; set; }

        public DbSet<Customer> Customers { get; set; }

        public DbSet<Sale> Sales { get; set; }

        public DbSet<SaleItem> SaleItems { get; set; }
        public DbSet<StockTransaction> StockTransactions { get; set; }
        public DbSet<Complaint> Complaints { get; set; }
        public DbSet<ComplaintCategory> ComplaintCategories { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Sale>()
                .HasIndex(s => s.CheckoutToken)
                .IsUnique()
                .HasFilter("[CheckoutToken] IS NOT NULL");

            // Restrict deleting Customer when historical Sales exist
            builder.Entity<Sale>()
                .HasOne(s => s.Customer)
                .WithMany(c => c.Sales)
                .HasForeignKey(s => s.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            // Restrict deleting Product when historical SaleItems exist
            builder.Entity<SaleItem>()
                .HasOne(si => si.Product)
                .WithMany(p => p.SaleItems)
                .HasForeignKey(si => si.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // Restrict deleting Product when historical StockTransactions exist
            builder.Entity<StockTransaction>()
                .HasOne(st => st.Product)
                .WithMany(p => p.StockTransactions)
                .HasForeignKey(st => st.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // Restrict deleting Customer when historical Complaints exist
            builder.Entity<Complaint>()
                .HasOne(c => c.Customer)
                .WithMany()
                .HasForeignKey(c => c.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            // Concurrency-safe Complaint Code sequence and unique index
            builder.HasSequence<int>("ComplaintCodeSequence")
                .StartsAt(311)
                .IncrementsBy(1);

            builder.Entity<Complaint>()
                .HasIndex(c => c.ComplaintCode)
                .IsUnique();
        }
    }
}