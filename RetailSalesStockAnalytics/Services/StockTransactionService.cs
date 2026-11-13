using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Services
{
    public class StockTransactionService : IStockTransactionService
    {
        private readonly ApplicationDbContext _context;

        public StockTransactionService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<StockTransactionViewModel>> GetTransactionsAsync()
        {
            return await _context.StockTransactions
                .Include(t => t.Product)
                .OrderByDescending(t => t.TransactionDate)
                .Select(t => new StockTransactionViewModel
                {
                    StockTransactionId = t.StockTransactionId,

                    ProductName = t.Product != null
                        ? t.Product.Name
                        : "Unknown Product",

                    Quantity = t.Quantity,

                    TransactionType = t.TransactionType,

                    TransactionDate = t.TransactionDate,

                    UnitCost = t.UnitCost,

                    Note = t.Note,

                    TotalCost = Math.Abs(t.Quantity) * t.UnitCost
                })
                .ToListAsync();
        }
    }
}