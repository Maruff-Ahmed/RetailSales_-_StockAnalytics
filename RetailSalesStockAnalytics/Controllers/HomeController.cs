using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IDashboardService _dashboardService;

        public HomeController(
            ILogger<HomeController> logger,
            IDashboardService dashboardService)
        {
            _logger = logger;
            _dashboardService = dashboardService;
        }


        // =====================================================
        // DASHBOARD
        // =====================================================

        public async Task<IActionResult> Index()
        {
            var model = await _dashboardService.GetDashboardAsync();
            return View(model);
        }


        // =====================================================
        // PRIVACY
        // =====================================================

        [AllowAnonymous]
        public IActionResult Privacy()
        {
            return View();
        }


        // =====================================================
        // ERROR
        // =====================================================

        [AllowAnonymous]
        [ResponseCache(
            Duration = 0,
            Location = ResponseCacheLocation.None,
            NoStore = true)]
        public IActionResult Error()
        {
            var exceptionHandlerPathFeature =
                HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();

            if (exceptionHandlerPathFeature?.Error != null)
            {
                _logger.LogError(
                    exceptionHandlerPathFeature.Error,
                    "Unhandled exception occurred while processing request path: {Path}",
                    exceptionHandlerPathFeature.Path);
            }

            return View(
                new ErrorViewModel
                {
                    RequestId =
                        Activity.Current?.Id ??
                        HttpContext.TraceIdentifier
                });
        }
    }
}
