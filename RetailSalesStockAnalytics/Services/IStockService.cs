using System.Threading.Tasks;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public interface IStockService
    {
        Task<StockDashboardViewModel> GetStockDashboardAsync();

        Task<StockOperationResult> RestockAsync(
            int productId,
            int quantity,
            decimal unitCost,
            string? note);

        Task<StockOperationResult> AdjustStockAsync(
            int productId,
            int quantity,
            string reason);

        Task<StockOperationResult> RecordInitialStockAsync(
            int productId,
            int quantity,
            decimal unitCost);

        Task<StockOperationResult> RecordProductEditAdjustmentAsync(
            int productId,
            int oldQuantity,
            int newQuantity,
            decimal unitCost);
    }
}