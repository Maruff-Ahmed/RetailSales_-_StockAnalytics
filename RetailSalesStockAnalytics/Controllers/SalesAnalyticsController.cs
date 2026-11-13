using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class SalesAnalyticsController : Controller
    {
        private readonly ISalesAnalyticsService _analyticsService;
        private readonly ApplicationDbContext _context;

        public SalesAnalyticsController(
            ISalesAnalyticsService analyticsService,
            ApplicationDbContext context)
        {
            _analyticsService = analyticsService;
            _context = context;
        }

        public async Task<IActionResult> Index(
            DateTime? startDate,
            DateTime? endDate,
            int? categoryId)
        {
            DateTime start = startDate?.Date
                ?? DateTime.Today.AddDays(-30);

            DateTime end = endDate?.Date
                ?? DateTime.Today;

            if (start > end)
            {
                (start, end) = (end, start);
            }

            var analytics =
                await _analyticsService.GetAnalyticsAsync(
                    start,
                    end,
                    categoryId);

            ViewBag.Categories = new SelectList(
                _context.Categories,
                "CategoryId",
                "Name",
                categoryId);

            return View(analytics);
        }
    }
}