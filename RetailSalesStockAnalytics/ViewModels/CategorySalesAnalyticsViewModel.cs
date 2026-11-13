namespace RetailSalesStockAnalytics.ViewModels
{
    public class CategorySalesAnalyticsViewModel
    {
        public int CategoryId { get; set; }

        public string CategoryName { get; set; } = string.Empty;

        public decimal NetRevenue { get; set; }

        public decimal GrossProfit { get; set; }
    }
}