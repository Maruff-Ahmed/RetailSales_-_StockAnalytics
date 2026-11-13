namespace RetailSalesStockAnalytics.ViewModels
{
    public class StockTransactionViewModel
    {
        public int StockTransactionId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public string TransactionType { get; set; } = string.Empty;

        public DateTime TransactionDate { get; set; }

        public decimal UnitCost { get; set; }

        public string? Note { get; set; }

        public decimal TotalCost { get; set; }
    }
}