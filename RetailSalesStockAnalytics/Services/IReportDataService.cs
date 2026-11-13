using System;
using System.Threading.Tasks;
using RetailSalesStockAnalytics.ViewModels.Reports;

namespace RetailSalesStockAnalytics.Services
{
    public interface IReportDataService
    {
        Task<DailySalesReportViewModel> GetDailySalesReportAsync(
            DateTime? startDate,
            DateTime? endDate);

        Task<DailyDemandingProductReportViewModel> GetDailyDemandingProductsReportAsync(
            DateTime? startDate,
            DateTime? endDate);

        Task<StockMovementReportViewModel> GetStockMovementReportAsync(
            DateTime? startDate,
            DateTime? endDate);

        Task<ComplaintRoutingReportViewModel> GetComplaintRoutingReportAsync(
            DateTime? startDate,
            DateTime? endDate);
    }
}
