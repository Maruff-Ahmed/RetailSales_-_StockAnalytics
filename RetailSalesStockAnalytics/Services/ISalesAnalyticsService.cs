using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public interface ISalesAnalyticsService
    {
        Task<SalesAnalyticsViewModel> GetAnalyticsAsync(
            DateTime startDate,
            DateTime endDate,
            int? categoryId = null);
    }
}