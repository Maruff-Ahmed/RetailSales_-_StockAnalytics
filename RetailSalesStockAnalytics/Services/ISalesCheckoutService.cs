using System.Threading.Tasks;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public enum CheckoutTokenStatus
    {
        InFlight,
        Completed
    }

    public interface ISalesCheckoutService
    {
        Task<CheckoutResult> CreateSaleAsync(
            SaleCreateViewModel model,
            bool isModelStateValid = true);

        Task<SaleCancellationResult> CancelSaleAsync(int saleId);
    }
}

