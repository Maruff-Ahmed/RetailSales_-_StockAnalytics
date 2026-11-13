namespace RetailSalesStockAnalytics.ViewModels
{
    public class SalesAnalyticsViewModel
    {
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public int? CategoryId { get; set; }

        public string? CategoryName { get; set; }

        public decimal TotalRevenue { get; set; }

        public decimal TotalCost { get; set; }

        public decimal GrossProfit { get; set; }

        public decimal ProfitMargin { get; set; }

        public decimal AverageBasketValue { get; set; }

        public int TotalSales { get; set; }

        public List<DailySalesAnalyticsViewModel> DailySales { get; set; }
            = new();

        public List<CategorySalesAnalyticsViewModel> CategorySales { get; set; }
            = new();
    }
}