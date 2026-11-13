using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RetailSalesStockAnalytics.Models
{
    public class StockTransaction
    {
        public int StockTransactionId { get; set; }

        // Product
        [Required]
        public int ProductId { get; set; }

        public Product? Product { get; set; }

        // Quantity changed
        [Required]
        public int Quantity { get; set; }

        // Type of transaction
        [Required]
        [StringLength(50)]
        public string TransactionType { get; set; } = string.Empty;

        // Optional note
        [StringLength(250)]
        public string? Note { get; set; }

        // Date of transaction
        public DateTime TransactionDate { get; set; }

        // Purchase price at the time of stock entry
        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitCost { get; set; }
    }
}