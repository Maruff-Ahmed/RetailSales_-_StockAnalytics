
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailSalesStockAnalytics.Services;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class CustomerPurchasePatternController : Controller
    {
        private readonly CustomerPurchasePatternService _service;

        public CustomerPurchasePatternController(
            CustomerPurchasePatternService service)
        {
            _service = service;
        }


        // ==========================================
        // CUSTOMER PURCHASE PATTERN DASHBOARD
        // ==========================================

        public async Task<IActionResult> Index(
            DateTime? startDate,
            DateTime? endDate)
        {
            // Default date range:
            // Current month

            DateTime start =
                startDate?.Date
                ?? new DateTime(
                    DateTime.Today.Year,
                    DateTime.Today.Month,
                    1);


            DateTime end =
                endDate?.Date
                ?? DateTime.Today;

            if (start > end)
            {
                (start, end) = (end, start);
            }


            var model =
                await _service.GetPurchasePatternAsync(
                    start,
                    end);


            return View(model);
        }
    }
}
