
using System;
using System.Collections.Generic;

namespace RetailSalesStockAnalytics.ViewModels.Reports
{
    public class ComplaintRoutingReportViewModel
    {
        // ==============================
        // FILTER
        // ==============================

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }


        // ==============================
        // SUMMARY
        // ==============================

        public int TotalComplaints { get; set; }

        public int RoutedComplaints { get; set; }

        public int PendingComplaints { get; set; }

        public int ManualReviewComplaints { get; set; }

        public decimal AverageSimilarityScore { get; set; }


        // ==============================
        // COMPLAINT LOG
        // ==============================

        public List<ComplaintRoutingRow> Complaints { get; set; }
            = new List<ComplaintRoutingRow>();
    }


    public class ComplaintRoutingRow
    {
        public string ComplaintCode { get; set; }
            = string.Empty;

        public DateTime CreatedAt { get; set; }

        public string CustomerName { get; set; }
            = string.Empty;

        public string ComplaintText { get; set; }
            = string.Empty;

        public string CategoryName { get; set; }
            = "Not Assigned";

        public decimal SimilarityScore { get; set; }

        public string RoutingStatus { get; set; }
            = "Pending";
    }
}

