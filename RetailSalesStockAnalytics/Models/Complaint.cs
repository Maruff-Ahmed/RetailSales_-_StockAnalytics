
using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.Models
{
    public class Complaint
    {
        public int ComplaintId { get; set; }

        [Required]
        [StringLength(20)]
        public string ComplaintCode { get; set; } = string.Empty;

        // Customer
        public int CustomerId { get; set; }

        public Customer? Customer { get; set; }


        // Original complaint text
        [Required]
        public string ComplaintText { get; set; } = string.Empty;


        // AI routing result
        public int? ComplaintCategoryId { get; set; }

        public ComplaintCategory? ComplaintCategory { get; set; }


        // Similarity score
        public decimal SimilarityScore { get; set; }


        // Routing status
        [StringLength(50)]
        public string RoutingStatus { get; set; } = "Pending";


        // When complaint was created
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}

