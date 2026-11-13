namespace RetailSalesStockAnalytics.ViewModels
{
    public class DailySalesAnalyticsViewModel
    {
        public DateTime Date { get; set; }

        public decimal NetRevenue { get; set; }

        public decimal GrossProfit { get; set; }
    }
}