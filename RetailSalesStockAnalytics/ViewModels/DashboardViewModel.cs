using System;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class DashboardViewModel
    {
        // =====================================================
        // TOP SUMMARY CARDS
        // =====================================================

        public decimal TodayNetRevenue { get; set; }

        public int TodaySalesCount { get; set; }

        public int TotalStockQuantity { get; set; }

        public int LowStockCount { get; set; }


        // =====================================================
        // SALES TREND
        // =====================================================

        public List<DailySalesViewModel> SalesTrend { get; set; } = new();


        // =====================================================
        // SALES BY CATEGORY
        // =====================================================

        public List<CategorySalesViewModel> SalesByCategory { get; set; } = new();


        // =====================================================
        // TOP PRODUCTS
        // =====================================================

        public List<TopProductViewModel> TopProducts { get; set; } = new();


        // =====================================================
        // STOCK MOVEMENT
        // =====================================================

        public int StockInQuantity { get; set; }

        public int StockOutQuantity { get; set; }


        // =====================================================
        // RECENT SALES
        // =====================================================

        public List<RecentSaleViewModel> RecentSales { get; set; } = new();


        // =====================================================
        // LOW STOCK PRODUCTS
        // =====================================================

        public List<LowStockProductViewModel> LowStockProducts { get; set; } = new();


        // =====================================================
        // AI COMPLAINT SUMMARY
        // =====================================================

        public int TotalComplaints { get; set; }

        public int RoutedComplaints { get; set; }

        public int PendingComplaints { get; set; }
    }



    // =========================================================
    // DAILY SALES
    // =========================================================

    public class DailySalesViewModel
    {
        public DateTime Date { get; set; }

        public decimal Revenue { get; set; }

        public int SalesCount { get; set; }
    }



    // =========================================================
    // CATEGORY SALES
    // =========================================================

    public class CategorySalesViewModel
    {
        public string CategoryName { get; set; } = string.Empty;

        public decimal Revenue { get; set; }

        public int QuantitySold { get; set; }
    }



    // =========================================================
    // TOP PRODUCT
    // =========================================================

    public class TopProductViewModel
    {
        public string ProductName { get; set; } = string.Empty;

        public int QuantitySold { get; set; }

        public decimal Revenue { get; set; }
    }



    // =========================================================
    // RECENT SALE
    // =========================================================

    public class RecentSaleViewModel
    {
        public int SaleId { get; set; }

        public string CustomerName { get; set; } = string.Empty;

        public DateTime SaleDate { get; set; }

        public decimal TotalAmount { get; set; }
    }



    // =========================================================
    // LOW STOCK PRODUCT
    // =========================================================

    public class LowStockProductViewModel
    {
        public string ProductName { get; set; } = string.Empty;

        public int StockQuantity { get; set; }

        public int ReorderLevel { get; set; }
    }
}