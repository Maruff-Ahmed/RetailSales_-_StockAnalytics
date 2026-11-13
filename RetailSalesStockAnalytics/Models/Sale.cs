using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RetailSalesStockAnalytics.Models
{
    public class Sale
    {
        public int SaleId { get; set; }

        // Foreign Key
        public int CustomerId { get; set; }

        // Navigation Property
        public Customer? Customer { get; set; }

        public DateTime SaleDate { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalAmount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Discount { get; set; }

        [StringLength(64)]
        public string? CheckoutToken { get; set; }

        // Navigation Property
        public ICollection<SaleItem> SaleItems { get; set; } = new List<SaleItem>();
    }
}