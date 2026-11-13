
using System;

namespace RetailSalesStockAnalytics.ViewModels
{
    public class ReportViewModel
    {
        // ==============================
        // FILTER
        // ==============================

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }


        // ==============================
        // SALES
        // ==============================

        public int TotalSales { get; set; }

        public decimal TotalRevenue { get; set; }

        public decimal TotalCost { get; set; }

        public decimal TotalProfit { get; set; }


        // ==============================
        // INVENTORY
        // ==============================

        public int TotalProducts { get; set; }

        public int LowStockProducts { get; set; }


        // ==============================
        // CUSTOMERS
        // ==============================

        public int TotalCustomers { get; set; }

        public int RepeatCustomers { get; set; }

        public decimal RepeatCustomerPercentage { get; set; }


        // ==============================
        // COMPLAINTS
        // ==============================

        public int TotalComplaints { get; set; }

        public int RoutedComplaints { get; set; }

        public int ManualReviewComplaints { get; set; }
    }
}

