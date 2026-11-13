
using System;
using System.Collections.Generic;

namespace RetailSalesStockAnalytics.ViewModels.Reports
{
    public class DailyDemandingProductReportViewModel
    {
        // ==============================
        // FILTER
        // ==============================

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }


        // ==============================
        // SUMMARY
        // ==============================

        public int TotalProductsSold { get; set; }

        public int TotalUnitsSold { get; set; }

        public decimal TotalRevenue { get; set; }


        // ==============================
        // PRODUCT REPORT
        // ==============================

        public List<DemandingProductRow> Products { get; set; }
            = new List<DemandingProductRow>();
    }


    public class DemandingProductRow
    {
        public DateTime Date { get; set; }

        public string ProductName { get; set; }
            = string.Empty;

        public string CategoryName { get; set; }
            = string.Empty;

        public int UnitsSold { get; set; }

        public decimal Revenue { get; set; }

        public decimal DemandPercentage { get; set; }
    }
}

