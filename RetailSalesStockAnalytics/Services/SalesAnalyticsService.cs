using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class SalesAnalyticsService : ISalesAnalyticsService
    {
        private readonly ApplicationDbContext _context;

        public SalesAnalyticsService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<SalesAnalyticsViewModel> GetAnalyticsAsync(
            DateTime startDate,
            DateTime endDate,
            int? categoryId = null)
        {
            // Make the end date inclusive
            DateTime endDateExclusive = endDate.Date.AddDays(1);

            // Load sales and their line items within the requested date range
            var sales = await _context.Sales
                .AsNoTracking()
                .Include(s => s.SaleItems)
                    .ThenInclude(si => si.Product)
                        .ThenInclude(p => p!.Category)
                .Where(s =>
                    s.SaleDate >= startDate.Date &&
                    s.SaleDate < endDateExclusive)
                .ToListAsync();

            // Fetch category name if filtered
            string? categoryName = null;
            if (categoryId.HasValue)
            {
                categoryName = await _context.Categories
                    .AsNoTracking()
                    .Where(c => c.CategoryId == categoryId.Value)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync();
            }

            // Intermediate collection to store category-level slices for each individual sale
            var saleCategorySlices = new List<SaleCategorySlice>();

            foreach (var sale in sales)
            {
                var validItems = sale.SaleItems
                    .Where(si => si.Product != null)
                    .ToList();

                if (validItems.Count == 0)
                {
                    continue;
                }

                // Total gross amount of this specific sale before discounts (rounded to 2 decimal places)
                decimal totalSaleGross = Math.Round(validItems.Sum(si => si.UnitPrice * si.Quantity), 2, MidpointRounding.AwayFromZero);
                decimal saleDiscount = Math.Round(Math.Max(0m, sale.Discount), 2, MidpointRounding.AwayFromZero);

                // Group items in this individual sale by Category in a deterministic, stable order (by CategoryId)
                var categoryGroups = validItems
                    .GroupBy(si => new
                    {
                        CategoryId = si.Product!.CategoryId,
                        CategoryName = si.Product.Category?.Name ?? "Uncategorized"
                    })
                    .OrderBy(g => g.Key.CategoryId)
                    .ToList();

                int categoryCount = categoryGroups.Count;
                decimal allocatedDiscountSoFar = 0m;

                for (int i = 0; i < categoryCount; i++)
                {
                    var catGroup = categoryGroups[i];

                    decimal catGrossInSale = Math.Round(catGroup.Sum(si => si.UnitPrice * si.Quantity), 2, MidpointRounding.AwayFromZero);
                    decimal catCostInSale = Math.Round(catGroup.Sum(si => si.UnitCost * si.Quantity), 2, MidpointRounding.AwayFromZero);

                    decimal catDiscountInSale = 0m;

                    if (totalSaleGross > 0 && saleDiscount > 0)
                    {
                        if (i == categoryCount - 1)
                        {
                            // Deterministic residual allocation: assign exact remaining discount to the final category
                            catDiscountInSale = Math.Max(0m, saleDiscount - allocatedDiscountSoFar);
                        }
                        else
                        {
                            // Proportional share rounded to 2 decimal places using MidpointRounding.AwayFromZero
                            decimal rawShare = saleDiscount * (catGrossInSale / totalSaleGross);
                            decimal roundedShare = Math.Round(rawShare, 2, MidpointRounding.AwayFromZero);

                            // Guard against allocating more than remaining discount or negative amounts
                            catDiscountInSale = Math.Max(0m, Math.Min(roundedShare, saleDiscount - allocatedDiscountSoFar));
                            allocatedDiscountSoFar += catDiscountInSale;
                        }
                    }

                    decimal catNetInSale = catGrossInSale - catDiscountInSale;
                    decimal catProfitInSale = catNetInSale - catCostInSale;

                    saleCategorySlices.Add(new SaleCategorySlice
                    {
                        SaleId = sale.SaleId,
                        SaleDate = sale.SaleDate,
                        CategoryId = catGroup.Key.CategoryId,
                        CategoryName = catGroup.Key.CategoryName,
                        GrossSales = catGrossInSale,
                        Discount = catDiscountInSale,
                        NetRevenue = catNetInSale,
                        TotalCost = catCostInSale,
                        GrossProfit = catProfitInSale
                    });
                }
            }

            // =====================================================
            // 1. FILTERING & OVERALL AGGREGATION
            // =====================================================

            // If a category filter is active, evaluate only slices for that category;
            // otherwise, evaluate all slices across all categories.
            var activeSlices = categoryId.HasValue
                ? saleCategorySlices.Where(s => s.CategoryId == categoryId.Value).ToList()
                : saleCategorySlices;

            // Net revenue, total cost, and gross profit
            decimal netRevenue = activeSlices.Sum(s => s.NetRevenue);
            decimal totalCost = activeSlices.Sum(s => s.TotalCost);
            decimal grossProfit = netRevenue - totalCost;

            decimal profitMargin = netRevenue > 0
                ? Math.Round((grossProfit / netRevenue) * 100m, 2, MidpointRounding.AwayFromZero)
                : 0m;

            // Distinct sales represented in the active slices
            int totalSales = activeSlices
                .Select(s => s.SaleId)
                .Distinct()
                .Count();

            decimal averageBasketValue = totalSales > 0
                ? Math.Round(netRevenue / totalSales, 2, MidpointRounding.AwayFromZero)
                : 0m;

            // =====================================================
            // 2. DAILY SALES ANALYTICS
            // =====================================================

            var dailySales = activeSlices
                .GroupBy(s => s.SaleDate.Date)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    decimal dayNetRevenue = g.Sum(s => s.NetRevenue);
                    decimal dayCost = g.Sum(s => s.TotalCost);
                    decimal dayProfit = dayNetRevenue - dayCost;

                    return new DailySalesAnalyticsViewModel
                    {
                        Date = g.Key,
                        NetRevenue = dayNetRevenue,
                        GrossProfit = dayProfit
                    };
                })
                .ToList();

            // =====================================================
            // 3. CATEGORY SALES ANALYTICS
            // =====================================================

            // If filtered, shows the single filtered category; if unfiltered, shows breakdown across all categories
            var categorySales = activeSlices
                .GroupBy(s => new
                {
                    s.CategoryId,
                    s.CategoryName
                })
                .Select(g =>
                {
                    decimal catNet = g.Sum(s => s.NetRevenue);
                    decimal catCost = g.Sum(s => s.TotalCost);
                    decimal catProfit = catNet - catCost;

                    return new CategorySalesAnalyticsViewModel
                    {
                        CategoryId = g.Key.CategoryId,
                        CategoryName = g.Key.CategoryName,
                        NetRevenue = catNet,
                        GrossProfit = catProfit
                    };
                })
                .OrderByDescending(c => c.NetRevenue)
                .ToList();

            // =====================================================
            // 4. RETURN ANALYTICS VIEW MODEL
            // =====================================================

            return new SalesAnalyticsViewModel
            {
                StartDate = startDate.Date,
                EndDate = endDate.Date,
                CategoryId = categoryId,
                CategoryName = categoryName,
                TotalRevenue = netRevenue,
                TotalCost = totalCost,
                GrossProfit = grossProfit,
                ProfitMargin = profitMargin,
                AverageBasketValue = averageBasketValue,
                TotalSales = totalSales,
                DailySales = dailySales,
                CategorySales = categorySales
            };
        }

        /// <summary>
        /// Internal record representing a specific category's slice of an individual sale transaction.
        /// </summary>
        private sealed class SaleCategorySlice
        {
            public int SaleId { get; init; }
            public DateTime SaleDate { get; init; }
            public int CategoryId { get; init; }
            public string CategoryName { get; init; } = string.Empty;
            public decimal GrossSales { get; init; }
            public decimal Discount { get; init; }
            public decimal NetRevenue { get; init; }
            public decimal TotalCost { get; init; }
            public decimal GrossProfit { get; init; }
        }
    }
}
