using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Models;

namespace RetailSalesStockAnalytics.Data
{
    public static class ComplaintCategorySeeder
    {
        public static async Task SeedAsync(
            ApplicationDbContext context)
        {
            // =========================================================
            // 1. ENSURE REQUIRED PRODUCT CATEGORIES EXIST
            // =========================================================

            var requiredProductCategories = new[]
            {
                "Grocery",
                "Beverages",
                "Electronics",
                "Cosmetics",
                "Clothing"
            };

            var existingCategories = await context.Categories.ToListAsync();

            var categoryMap = existingCategories.ToDictionary(
                c => c.Name.Trim(),
                c => c,
                StringComparer.OrdinalIgnoreCase);

            bool newCategoriesAdded = false;

            foreach (var categoryName in requiredProductCategories)
            {
                if (!categoryMap.ContainsKey(categoryName))
                {
                    var newCategory = new Category
                    {
                        Name = categoryName
                    };

                    context.Categories.Add(newCategory);
                    categoryMap[categoryName] = newCategory;
                    newCategoriesAdded = true;
                }
            }

            if (newCategoriesAdded)
            {
                await context.SaveChangesAsync();

                // Refresh existing categories so all generated CategoryIds are available
                existingCategories = await context.Categories.ToListAsync();

                categoryMap = existingCategories.ToDictionary(
                    c => c.Name.Trim(),
                    c => c,
                    StringComparer.OrdinalIgnoreCase);
            }


            // =========================================================
            // 2. DEFINE COMPLAINT CATEGORY DEFINITIONS
            // =========================================================

            var definitions = new[]
            {
                // ==========================================
                // GROCERY
                // ==========================================
                new
                {
                    ProductCategoryName = "Grocery",
                    CategoryName = "Food Quality Fault",
                    DescriptionTerms = "spoiled, rotten, expired, stale, smell, insect, damaged",
                    Department = "Grocery Support",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Grocery",
                    CategoryName = "Grocery Packaging Fault",
                    DescriptionTerms = "package, packet, seal, torn, leakage, broken, damaged",
                    Department = "Packaging",
                    SimilarityThreshold = 0.80m
                },

                // ==========================================
                // BEVERAGES
                // ==========================================
                new
                {
                    ProductCategoryName = "Beverages",
                    CategoryName = "Beverage Quality Fault",
                    DescriptionTerms = "expired, stale, taste, smell, contaminated, bad, spoiled",
                    Department = "Beverage Support",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Beverages",
                    CategoryName = "Beverage Packaging Fault",
                    DescriptionTerms = "bottle, cap, seal, leakage, broken, damaged, container",
                    Department = "Packaging",
                    SimilarityThreshold = 0.80m
                },

                // ==========================================
                // ELECTRONICS
                // ==========================================
                new
                {
                    ProductCategoryName = "Electronics",
                    CategoryName = "Electrical Fault",
                    DescriptionTerms = "power, dead, switch, burnt, fuse",
                    Department = "Electrical Repair",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Electronics",
                    CategoryName = "Display Fault",
                    DescriptionTerms = "screen, display, black, flicker, cracked, pixels",
                    Department = "Electronics Repair",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Electronics",
                    CategoryName = "Battery Fault",
                    DescriptionTerms = "battery, charging, charge, drain, charging-port",
                    Department = "Electronics Repair",
                    SimilarityThreshold = 0.80m
                },

                // ==========================================
                // COSMETICS
                // ==========================================
                new
                {
                    ProductCategoryName = "Cosmetics",
                    CategoryName = "Cosmetic Product Quality Fault",
                    DescriptionTerms = "expired, smell, texture, color, reaction, damaged",
                    Department = "Cosmetics Support",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Cosmetics",
                    CategoryName = "Cosmetic Packaging Fault",
                    DescriptionTerms = "bottle, container, seal, leakage, broken, damaged",
                    Department = "Packaging",
                    SimilarityThreshold = 0.80m
                },

                // ==========================================
                // CLOTHING
                // ==========================================
                new
                {
                    ProductCategoryName = "Clothing",
                    CategoryName = "Clothing Quality Fault",
                    DescriptionTerms = "torn, ripped, stain, size, color, damaged",
                    Department = "Clothing Support",
                    SimilarityThreshold = 0.80m
                },
                new
                {
                    ProductCategoryName = "Clothing",
                    CategoryName = "Clothing Packaging Fault",
                    DescriptionTerms = "package, packet, torn, damaged, seal",
                    Department = "Packaging",
                    SimilarityThreshold = 0.80m
                }
            };


            // =========================================================
            // 3. IDEMPOTENT SEEDING WITH DYNAMIC FOREIGN KEY RESOLUTION
            // =========================================================

            var existingComplaintCategories = await context.ComplaintCategories
                .Select(cc => new { cc.CategoryId, cc.CategoryName })
                .ToListAsync();

            var existingLookup = new HashSet<string>(
                existingComplaintCategories.Select(cc => $"{cc.CategoryId}:{cc.CategoryName.Trim()}"),
                StringComparer.OrdinalIgnoreCase);

            var complaintCategoriesToAdd = new List<ComplaintCategory>();

            foreach (var def in definitions)
            {
                // Safely resolve product category foreign key
                if (!categoryMap.TryGetValue(def.ProductCategoryName, out var productCategory) || productCategory == null)
                {
                    // Category missing or unexpected; safely skip
                    continue;
                }

                string lookupKey = $"{productCategory.CategoryId}:{def.CategoryName.Trim()}";

                // Idempotency: skip if already present
                if (existingLookup.Contains(lookupKey))
                {
                    continue;
                }

                complaintCategoriesToAdd.Add(new ComplaintCategory
                {
                    CategoryId = productCategory.CategoryId,
                    CategoryName = def.CategoryName,
                    DescriptionTerms = def.DescriptionTerms,
                    Department = def.Department,
                    SimilarityThreshold = def.SimilarityThreshold
                });

                existingLookup.Add(lookupKey);
            }

            if (complaintCategoriesToAdd.Count > 0)
            {
                await context.ComplaintCategories.AddRangeAsync(complaintCategoriesToAdd);
                await context.SaveChangesAsync();
            }
        }
    }
}
