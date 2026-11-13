
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;
using RetailSalesStockAnalytics.ViewModels.Reports;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class ReportsController : Controller
    {
        private readonly ReportExportService _reportExportService;
        private readonly IReportDataService _reportDataService;

        public ReportsController(
            ReportExportService reportExportService,
            IReportDataService reportDataService)
        {
            _reportExportService = reportExportService;
            _reportDataService = reportDataService;
        }


        // ==========================================================
        // DAILY SALES REPORT
        // ==========================================================

        public async Task<IActionResult> DailySales(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailySalesReportAsync(startDate, endDate);

            return View(model);
        }


        // ==========================================================
        // DAILY DEMANDING PRODUCTS REPORT
        // ==========================================================

        public async Task<IActionResult> DailyDemandingProducts(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailyDemandingProductsReportAsync(startDate, endDate);

            return View(model);
        }


        // ==========================================================
        // STOCK MOVEMENT REPORT
        // ==========================================================

        public async Task<IActionResult> StockMovement(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetStockMovementReportAsync(startDate, endDate);

            return View(model);
        }


        // ==========================================================
        // AI COMPLAINT ROUTING REPORT
        // ==========================================================

        public async Task<IActionResult> ComplaintRouting(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetComplaintRoutingReportAsync(startDate, endDate);

            return View(model);
        }


        // ==========================================================
        // REPORT DASHBOARD
        // ==========================================================

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }


        // ==========================================================
        // STOCK MOVEMENT - EXCEL
        // ==========================================================

        public async Task<IActionResult> ExportStockMovementExcel(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetStockMovementReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportStockMovementExcel(model);

            return File(
                file,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Stock-Movement-Report.xlsx");
        }


        // ==========================================================
        // STOCK MOVEMENT - PDF
        // ==========================================================

        public async Task<IActionResult> ExportStockMovementPdf(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetStockMovementReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportStockMovementPdf(model);

            return File(
                file,
                "application/pdf",
                "Stock-Movement-Report.pdf");
        }
        
// ==========================================================
// DAILY SALES - EXCEL
// ==========================================================

        public async Task<IActionResult> ExportDailySalesExcel(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailySalesReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportDailySalesExcel(model);

            return File(
                file,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Daily-Sales-Report.xlsx");
        }


        // ==========================================================
        // DAILY SALES - PDF
        // ==========================================================

        public async Task<IActionResult> ExportDailySalesPdf(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailySalesReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportDailySalesPdf(model);

            return File(
                file,
                "application/pdf",
                "Daily-Sales-Report.pdf");
        }
      
// ==========================================================
// DAILY DEMANDING PRODUCTS - EXCEL
// ==========================================================

        public async Task<IActionResult> ExportDailyDemandingProductsExcel(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailyDemandingProductsReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportDailyDemandingProductsExcel(model);

            return File(
                file,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Daily-Demanding-Products-Report.xlsx");
        }


        // ==========================================================
        // DAILY DEMANDING PRODUCTS - PDF
        // ==========================================================

        public async Task<IActionResult> ExportDailyDemandingProductsPdf(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetDailyDemandingProductsReportAsync(startDate, endDate);

            var file =
                _reportExportService
                    .ExportDailyDemandingProductsPdf(model);

            return File(
                file,
                "application/pdf",
                "Daily-Demanding-Products-Report.pdf");
        }
       
        // ==========================================================
        // AI COMPLAINT ROUTING - EXCEL
        // ==========================================================

        public async Task<IActionResult> ExportComplaintRoutingExcel(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetComplaintRoutingReportAsync(startDate, endDate);

            var file = _reportExportService
                .ExportComplaintRoutingExcel(model);

            return File(
                file,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "AI-Complaint-Routing-Report.xlsx");
        }


        // ==========================================================
        // AI COMPLAINT ROUTING - PDF
        // ==========================================================

        public async Task<IActionResult> ExportComplaintRoutingPdf(
            DateTime? startDate,
            DateTime? endDate)
        {
            var model = await _reportDataService
                .GetComplaintRoutingReportAsync(startDate, endDate);

            var file = _reportExportService
                .ExportComplaintRoutingPdf(model);

            return File(
                file,
                "application/pdf",
                "AI-Complaint-Routing-Report.pdf");
        }



    }
}

