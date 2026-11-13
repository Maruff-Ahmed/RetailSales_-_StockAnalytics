using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.Models
{
    public class Customer
    {
        public int CustomerId { get; set; }

        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(20)]
        public string? Phone { get; set; }

        [EmailAddress]
        [StringLength(150)]
        public string? Email { get; set; }

        // Navigation Property
        public ICollection<Sale> Sales { get; set; } = new List<Sale>();
    }
}