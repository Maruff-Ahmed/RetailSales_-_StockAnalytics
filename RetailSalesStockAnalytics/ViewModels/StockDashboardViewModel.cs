namespace RetailSalesStockAnalytics.ViewModels
{
    public class StockDashboardViewModel
    {
        public int TotalProducts { get; set; }

        public int TotalStockUnits { get; set; }

        public decimal TotalInventoryValue { get; set; }

        public int LowStockProducts { get; set; }

        public int OutOfStockProducts { get; set; }

        public List<StockProductViewModel> Products { get; set; }
            = new List<StockProductViewModel>();
    }

    public class StockProductViewModel
    {
        public int ProductId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public string CategoryName { get; set; } = string.Empty;

        public int StockQuantity { get; set; }

        public int ReorderLevel { get; set; }

        public decimal PurchasePrice { get; set; }

        public decimal InventoryValue { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}