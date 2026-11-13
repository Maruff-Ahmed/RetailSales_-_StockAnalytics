
using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class SaleCreateViewModel
    {
        // =====================================================
        // CUSTOMER INFORMATION
        // =====================================================

        [Required(ErrorMessage = "Customer name is required.")]
        [StringLength(100)]
        public string CustomerName { get; set; } = string.Empty;

        [StringLength(20)]
        public string? CustomerPhone { get; set; }

        [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
        [StringLength(150)]
        public string? CustomerEmail { get; set; }


        // =====================================================
        // SALE INFORMATION
        // =====================================================

        [Required]
        public DateTime SaleDate { get; set; } = DateTime.Now;

        [Range(
            0,
            double.MaxValue,
            ErrorMessage = "Discount cannot be negative.")]
        public decimal Discount { get; set; }

        public string? CheckoutToken { get; set; }


        // =====================================================
        // SALE ITEMS
        // =====================================================

        public List<SaleItemViewModel> Items { get; set; } = new();
    }
}

