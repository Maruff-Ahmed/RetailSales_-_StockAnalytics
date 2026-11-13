
namespace RetailSalesStockAnalytics.ViewModels
{
    public class CustomerPurchasePatternViewModel
    {
        // ==============================
        // FILTER
        // ==============================

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }


        // ==============================
        // CUSTOMER SUMMARY
        // ==============================

        public int TotalCustomers { get; set; }

        public int RepeatCustomers { get; set; }

        public decimal RepeatCustomerPercentage { get; set; }


        // ==============================
        // CUSTOMER ANALYSIS
        // ==============================

        public List<CustomerPurchaseAnalysisViewModel> Customers
        {
            get;
            set;
        } = new();


        // ==============================
        // PEAK HOUR ANALYSIS
        // ==============================

        public List<PurchaseHourAnalysisViewModel> HourlyAnalysis
        {
            get;
            set;
        } = new();


        // ==============================
        // PRODUCT ANALYSIS
        // ==============================

        public List<ProductPurchaseAnalysisViewModel> Products
        {
            get;
            set;
        } = new();
    }


    // ==========================================
    // CUSTOMER PURCHASE ANALYSIS
    // ==========================================

    public class CustomerPurchaseAnalysisViewModel
    {
        public int CustomerId { get; set; }

        public string CustomerName { get; set; } = string.Empty;

        public int PurchaseCount { get; set; }

        public decimal PurchaseFrequencyPercentage { get; set; }

        public decimal TotalSpent { get; set; }

        public decimal RevenueContributionPercentage { get; set; }

        public string CustomerType { get; set; } = string.Empty;
    }


    // ==========================================
    // HOURLY PURCHASE ANALYSIS
    // ==========================================

    public class PurchaseHourAnalysisViewModel
    {
        public int Hour { get; set; }

        public int SalesCount { get; set; }

        public decimal Revenue { get; set; }

        public decimal SalesPercentage { get; set; }

        public decimal RevenuePercentage { get; set; }
    }


    // ==========================================
    // PRODUCT PURCHASE ANALYSIS
    // ==========================================

    public class ProductPurchaseAnalysisViewModel
    {
        public int ProductId { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public int QuantityPurchased { get; set; }

        public decimal PurchasePercentage { get; set; }

        public decimal Revenue { get; set; }

        public decimal RevenuePercentage { get; set; }
    }
}
