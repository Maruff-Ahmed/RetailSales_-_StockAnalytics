using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class StockRestockViewModel
    {
        [Required(ErrorMessage = "Please select a product.")]
        public int ProductId { get; set; }

        [Required(ErrorMessage = "Quantity is required.")]
        [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
        public int Quantity { get; set; }

        [Required(ErrorMessage = "Unit cost is required.")]
        [Range(0.01, double.MaxValue, ErrorMessage = "Unit cost must be greater than 0.")]
        public decimal UnitCost { get; set; }

        [StringLength(250)]
        public string? Note { get; set; }
    }
}