using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RetailSalesStockAnalytics.Models
{
    public class Product
    {
        public int ProductId { get; set; }

        [Required]
        [StringLength(150)]
        public string Name { get; set; } = string.Empty;

        // Foreign Key
        public int CategoryId { get; set; }

        // Navigation Property
        public Category? Category { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal PurchasePrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal SellingPrice { get; set; }

        public int StockQuantity { get; set; }

        public int ReorderLevel { get; set; }

        // Navigation Property
        public ICollection<SaleItem> SaleItems { get; set; } = new List<SaleItem>();
        public ICollection<StockTransaction> StockTransactions { get; set; }
    = new List<StockTransaction>();
    }
}