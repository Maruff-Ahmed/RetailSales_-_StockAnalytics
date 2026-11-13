using System.ComponentModel.DataAnnotations.Schema;

namespace RetailSalesStockAnalytics.Models
{
    public class SaleItem
    {
        public int SaleItemId { get; set; }

        // Foreign Key
        public int SaleId { get; set; }

        // Navigation Property
        public Sale? Sale { get; set; }

        // Foreign Key
        public int ProductId { get; set; }

        // Navigation Property
        public Product? Product { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal UnitCost { get; set; }
    }
}