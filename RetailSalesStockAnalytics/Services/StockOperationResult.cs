namespace RetailSalesStockAnalytics.Services
{
    public class StockOperationResult
    {
        public bool Success { get; set; }
        public bool NotFound { get; set; }
        public bool InvalidOperation { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorField { get; set; }
        public string? ProductName { get; set; }
        public int CurrentStock { get; set; }
        public int Quantity { get; set; }

        public static StockOperationResult Succeeded(string productName, int quantity, int currentStock) =>
            new()
            {
                Success = true,
                ProductName = productName,
                Quantity = quantity,
                CurrentStock = currentStock
            };

        public static StockOperationResult ProductNotFound(string message = "Selected product was not found.") =>
            new()
            {
                Success = false,
                NotFound = true,
                ErrorField = "ProductId",
                ErrorMessage = message
            };

        public static StockOperationResult Invalid(string message, string field = "Quantity", int currentStock = 0) =>
            new()
            {
                Success = false,
                InvalidOperation = true,
                ErrorField = field,
                ErrorMessage = message,
                CurrentStock = currentStock
            };

        public static StockOperationResult Failed(string message) =>
            new()
            {
                Success = false,
                ErrorMessage = message
            };
    }
}

