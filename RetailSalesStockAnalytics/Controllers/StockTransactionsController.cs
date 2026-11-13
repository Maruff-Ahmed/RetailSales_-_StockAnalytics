using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailSalesStockAnalytics.Services;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class StockTransactionsController : Controller
    {
        private readonly IStockTransactionService _transactionService;

        public StockTransactionsController(
            IStockTransactionService transactionService)
        {
            _transactionService = transactionService;
        }

        // GET: /StockTransactions
        public async Task<IActionResult> Index()
        {
            var transactions =
                await _transactionService.GetTransactionsAsync();

            return View(transactions);
        }
    }
}