
using System;
using System.Collections.Generic;

namespace RetailSalesStockAnalytics.ViewModels.Reports
{
    public class DailySalesReportViewModel
    {
        // ==============================
        // FILTER
        // ==============================

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }


        // ==============================
        // SUMMARY
        // ==============================

        public int TotalSales { get; set; }

        public int TotalUnitsSold { get; set; }

        public decimal TotalRevenue { get; set; }

        public decimal TotalCost { get; set; }

        public decimal TotalProfit { get; set; }


        // ==============================
        // DAILY REPORT
        // ==============================

        public List<DailySalesRow> DailySales { get; set; }
            = new List<DailySalesRow>();
    }


    public class DailySalesRow
    {
        public DateTime Date { get; set; }

        public int SalesCount { get; set; }

        public int UnitsSold { get; set; }

        public decimal Revenue { get; set; }

        public decimal Cost { get; set; }

        public decimal Profit { get; set; }
    }
}

