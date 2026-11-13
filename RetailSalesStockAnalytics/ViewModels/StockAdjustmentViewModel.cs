using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class StockAdjustmentViewModel
    {
        [Required(ErrorMessage = "Please select a product.")]
        public int ProductId { get; set; }

        [Required(ErrorMessage = "Adjustment quantity is required.")]
        public int Quantity { get; set; }

        [Required(ErrorMessage = "Please provide a reason.")]
        [StringLength(250)]
        public string Reason { get; set; } = string.Empty;
    }
}

