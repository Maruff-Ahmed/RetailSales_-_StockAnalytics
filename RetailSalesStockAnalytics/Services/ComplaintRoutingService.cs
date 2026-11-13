
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;

namespace RetailSalesStockAnalytics.Services
{
    public class ComplaintRoutingService
    {
        private readonly ApplicationDbContext _context;

        public ComplaintRoutingService(ApplicationDbContext context)
        {
            _context = context;
        }

        // ==========================================
        // STOP WORDS
        // ==========================================

        private readonly HashSet<string> _stopWords =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the",
                "is", "are", "was", "were",
                "am", "be", "been", "being",
                "and", "or", "but", "if",
                "then", "than",
                "this", "that", "these", "those",
                "it", "its",
                "i", "me", "my",
                "we", "our",
                "you", "your",
                "he", "she", "they", "them",
                "his", "her", "their",
                "to", "of", "in", "on",
                "at", "for", "from",
                "with", "by", "as",
                "about", "into", "over",
                "after", "before",
                "very", "completely",
                "all", "no", "not",
                "will", "can",
                "cannot", "have", "has", "had",
                "do", "does", "did", "so"
            };


        // ==========================================
        // MAIN ROUTING METHOD
        // ==========================================

        public async Task<ComplaintRoutingResult>
            RouteComplaintAsync(
                int productCategoryId,
                string complaintText)
        {
            var complaintTerms =
                ExtractDistinctTerms(complaintText);

            var complaintCategories =
                await _context.ComplaintCategories
                    .Where(c =>
                        c.CategoryId == productCategoryId)
                    .ToListAsync();

            if (!complaintCategories.Any())
            {
                return new ComplaintRoutingResult
                {
                    ComplaintCategory = null,
                    SimilarityScore = 0,
                    RoutingStatus = "Manual Review",
                    Department = "Manual Review",
                    CategoryScores = new List<CategorySimilarityResult>()
                };
            }


            // ==========================================
            // CALCULATE ALL CATEGORY SCORES
            // ==========================================

            var categoryScores =
                new List<CategorySimilarityResult>();

            ComplaintCategory? bestCategory = null;

            decimal bestSimilarity = 0;


            foreach (var category in complaintCategories)
            {
                var categoryTerms =
                    ExtractDistinctTerms(
                        category.DescriptionTerms);

                decimal similarity =
                    CalculateCosineSimilarity(
                        complaintTerms,
                        categoryTerms);

                categoryScores.Add(
                    new CategorySimilarityResult
                    {
                        CategoryName =
                            category.CategoryName,

                        Department =
                            category.Department,

                        SimilarityScore =
                            similarity,

                        Threshold =
                            category.SimilarityThreshold,

                        CommonTerms =
                            complaintTerms
                                .Intersect(
                                    categoryTerms,
                                    StringComparer.OrdinalIgnoreCase)
                                .ToList()
                    });


                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestCategory = category;
                }
            }


            // ==========================================
            // NO CATEGORY
            // ==========================================

            if (bestCategory == null)
            {
                return new ComplaintRoutingResult
                {
                    ComplaintCategory = null,
                    SimilarityScore = bestSimilarity,
                    RoutingStatus = "Manual Review",
                    Department = "Manual Review",
                    CategoryScores = categoryScores
                };
            }


            // ==========================================
            // THRESHOLD CHECK
            // ==========================================

            decimal threshold =
                bestCategory.SimilarityThreshold;


            if (bestSimilarity >= threshold)
            {
                return new ComplaintRoutingResult
                {
                    ComplaintCategory =
                        bestCategory,

                    SimilarityScore =
                        bestSimilarity,

                    RoutingStatus =
                        "Routed",

                    Department =
                        bestCategory.Department,

                    CategoryScores =
                        categoryScores
                            .OrderByDescending(
                                x => x.SimilarityScore)
                            .ToList()
                };
            }


            // ==========================================
            // MANUAL REVIEW
            // ==========================================

            return new ComplaintRoutingResult
            {
                ComplaintCategory =
                    bestCategory,

                SimilarityScore =
                    bestSimilarity,

                RoutingStatus =
                    "Manual Review",

                Department =
                    "Manual Review",

                CategoryScores =
                    categoryScores
                        .OrderByDescending(
                            x => x.SimilarityScore)
                        .ToList()
            };
        }


        // ==========================================
        // TOKENIZATION
        // ==========================================

        private HashSet<string> ExtractDistinctTerms(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new HashSet<string>();
            }

            text = text.ToLowerInvariant();

            text = Regex.Replace(
                text,
                @"[^a-z0-9\s]",
                " ");

            var words =
                text.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries);

            return words
                .Where(word =>
                    !_stopWords.Contains(word))
                .ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
        }


        // ==========================================
        // COSINE SIMILARITY
        // ==========================================

        private decimal CalculateCosineSimilarity(
            HashSet<string> complaintTerms,
            HashSet<string> categoryTerms)
        {
            if (!complaintTerms.Any() ||
                !categoryTerms.Any())
            {
                return 0;
            }

            int commonTerms =
                complaintTerms
                    .Intersect(
                        categoryTerms,
                        StringComparer.OrdinalIgnoreCase)
                    .Count();

            double complaintLength =
                Math.Sqrt(complaintTerms.Count);

            double categoryLength =
                Math.Sqrt(categoryTerms.Count);

            double similarity =
                commonTerms /
                (complaintLength * categoryLength);

            return (decimal)similarity;
        }
    }


    // ==========================================
    // MAIN ROUTING RESULT
    // ==========================================

    public class ComplaintRoutingResult
    {
        public ComplaintCategory?
            ComplaintCategory
        { get; set; }

        public decimal SimilarityScore
        {
            get;
            set;
        }

        public string RoutingStatus
        {
            get;
            set;
        } = string.Empty;

        public string Department
        {
            get;
            set;
        } = string.Empty;

        public List<CategorySimilarityResult>
            CategoryScores
        { get; set; }
            = new List<CategorySimilarityResult>();
    }


    // ==========================================
    // INDIVIDUAL CATEGORY SCORE
    // ==========================================

    public class CategorySimilarityResult
    {
        public string CategoryName
        {
            get;
            set;
        } = string.Empty;

        public string Department
        {
            get;
            set;
        } = string.Empty;

        public decimal SimilarityScore
        {
            get;
            set;
        }

        public decimal Threshold
        {
            get;
            set;
        }

        public List<string> CommonTerms
        {
            get;
            set;
        } = new List<string>();
    }
}
