
using System;
using System.Collections.Generic;

namespace RetailSalesStockAnalytics.ViewModels.Reports
{
    public class StockMovementReportViewModel
    {
        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        // Summary
        public int TotalTransactions { get; set; }

        public int TotalStockIn { get; set; }

        public int TotalStockOut { get; set; }

        public int TotalAdjustments { get; set; }

        public List<StockMovementRow> Movements { get; set; }
            = new List<StockMovementRow>();
    }


    public class StockMovementRow
    {
        public DateTime Date { get; set; }

        public string ProductName { get; set; }
            = string.Empty;

        public string TransactionType { get; set; }
            = string.Empty;

        public int Quantity { get; set; }

        public decimal UnitCost { get; set; }

        public decimal TotalCost { get; set; }

        public string? Note { get; set; }
    }
}

