using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class StockController : Controller
    {
        private readonly IStockService _stockService;
        private readonly ApplicationDbContext _context;

        public StockController(
            IStockService stockService,
            ApplicationDbContext context)
        {
            _stockService = stockService;
            _context = context;
        }

        // =====================================================
        // STOCK DASHBOARD
        // GET: /Stock
        // =====================================================

        public async Task<IActionResult> Index(string? status)
        {
            var model =
                await _stockService.GetStockDashboardAsync();

            if (!string.IsNullOrEmpty(status))
            {
                if (status == "Low")
                {
                    model.Products = model.Products
                        .Where(p => p.Status == "Low Stock")
                        .ToList();
                }
                else if (status == "Out")
                {
                    model.Products = model.Products
                        .Where(p => p.Status == "Out of Stock")
                        .ToList();
                }
            }

            ViewBag.Status = status;

            return View(model);
        }


        // =====================================================
        // RESTOCK - GET
        // GET: /Stock/Create
        // =====================================================

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            await LoadProducts();

            var model = new StockRestockViewModel
            {
                Quantity = 1,
                UnitCost = 0
            };

            return View(model);
        }


        // =====================================================
        // RESTOCK - POST
        // POST: /Stock/Create
        // =====================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            StockRestockViewModel model)
        {
            if (!ModelState.IsValid)
            {
                await LoadProducts(model.ProductId);

                return View(model);
            }

            var result = await _stockService.RestockAsync(
                model.ProductId,
                model.Quantity,
                model.UnitCost,
                model.Note);

            if (result.Success)
            {
                TempData["SuccessMessage"] =
                    $"{result.ProductName} restocked successfully. " +
                    $"{model.Quantity} units added.";

                return RedirectToAction(nameof(Index));
            }

            if (result.NotFound)
            {
                ModelState.AddModelError(
                    "ProductId",
                    result.ErrorMessage ?? "Selected product was not found.");
            }
            else
            {
                ModelState.AddModelError(
                    result.ErrorField ?? string.Empty,
                    result.ErrorMessage ?? "An error occurred while restocking.");
            }

            await LoadProducts(model.ProductId);

            return View(model);
        }
        // =====================================================
        // STOCK ADJUSTMENT - GET
        // GET: /Stock/Adjust
        // =====================================================

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Adjust()
        {
            await LoadProducts();

            var model = new StockAdjustmentViewModel();

            return View(model);
        }


        // =====================================================
        // STOCK ADJUSTMENT - POST
        // POST: /Stock/Adjust
        // =====================================================

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Adjust(
            StockAdjustmentViewModel model)
        {
            if (model.Quantity == 0)
            {
                ModelState.AddModelError(
                    "Quantity",
                    "Adjustment quantity cannot be zero.");

                await LoadProducts(model.ProductId);

                return View(model);
            }

            if (!ModelState.IsValid)
            {
                await LoadProducts(model.ProductId);

                return View(model);
            }

            var result = await _stockService.AdjustStockAsync(
                model.ProductId,
                model.Quantity,
                model.Reason);

            if (result.Success)
            {
                TempData["SuccessMessage"] =
                    $"{result.ProductName} stock adjusted successfully.";

                return RedirectToAction(nameof(Index));
            }

            if (result.NotFound)
            {
                ModelState.AddModelError(
                    "ProductId",
                    result.ErrorMessage ?? "Selected product was not found.");
            }
            else
            {
                ModelState.AddModelError(
                    result.ErrorField ?? "Quantity",
                    result.ErrorMessage ?? "An error occurred while adjusting stock.");
            }

            await LoadProducts(model.ProductId);

            return View(model);
        }


        // =====================================================
        // LOAD PRODUCT DROPDOWN
        // =====================================================

        private async Task LoadProducts(int? selectedProductId = null)
        {
            var products = await _context.Products
                .OrderBy(p => p.Name)
                .ToListAsync();

            ViewBag.Products = new SelectList(
                products,
                "ProductId",
                "Name",
                selectedProductId);
        }
    }
}