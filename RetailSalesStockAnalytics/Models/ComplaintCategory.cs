
using System.ComponentModel.DataAnnotations;

namespace RetailSalesStockAnalytics.Models
{
    public class ComplaintCategory
    {
        public int ComplaintCategoryId { get; set; }

        [Required]
        [StringLength(100)]
        public string CategoryName { get; set; } = string.Empty;

        [Required]
        public string DescriptionTerms { get; set; } = string.Empty;

        [Required]
        [StringLength(150)]
        public string Department { get; set; } = string.Empty;

        // Similarity threshold
        public decimal SimilarityThreshold { get; set; } = 0.80m;


        // Existing Product Category
        public int CategoryId { get; set; }

        public Category? Category { get; set; }


        // Navigation Property
        public ICollection<Complaint> Complaints { get; set; }
            = new List<Complaint>();
    }
}
