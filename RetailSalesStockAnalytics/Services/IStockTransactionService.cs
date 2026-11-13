using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public interface IStockTransactionService
    {
        Task<List<StockTransactionViewModel>> GetTransactionsAsync();
    }
}