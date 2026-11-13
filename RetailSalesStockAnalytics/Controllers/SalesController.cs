
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;
using RetailSalesStockAnalytics.ViewModels;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class SalesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ISalesCheckoutService _salesCheckoutService;
        private readonly ILogger<SalesController> _logger;

        public SalesController(
            ApplicationDbContext context,
            ISalesCheckoutService salesCheckoutService,
            ILogger<SalesController> logger)
        {
            _context = context;
            _salesCheckoutService = salesCheckoutService;
            _logger = logger;
        }


        // =========================================================
        // GET: Sales
        // =========================================================

        public async Task<IActionResult> Index(
            string? searchTerm,
            DateTime? startDate,
            DateTime? endDate,
            string? sortOrder,
            int pageNumber = SalesIndexViewModel.DefaultPageNumber,
            int pageSize = SalesIndexViewModel.DefaultPageSize)
        {
            // Normalize page size (allowed: 10, 25, 50; default: 25)
            if (!SalesIndexViewModel.AllowedPageSizes.Contains(pageSize))
            {
                pageSize = SalesIndexViewModel.DefaultPageSize;
            }

            // Normalize date range if start is after end
            if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
            {
                (startDate, endDate) = (endDate, startDate);
            }

            // Base query with Customer navigation included
            var query = _context.Sales
                .AsNoTracking()
                .Include(s => s.Customer)
                .AsQueryable();

            // Search filtering (Customer name or CheckoutToken)
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                string trimmed = searchTerm.Trim();
                query = query.Where(s =>
                    (s.Customer != null && s.Customer.Name.Contains(trimmed)) ||
                    (s.CheckoutToken != null && s.CheckoutToken.Contains(trimmed)));
            }

            // Date filtering
            if (startDate.HasValue)
            {
                DateTime start = startDate.Value.Date;
                query = query.Where(s => s.SaleDate >= start);
            }

            if (endDate.HasValue)
            {
                DateTime endExclusive = endDate.Value.Date.AddDays(1);
                query = query.Where(s => s.SaleDate < endExclusive);
            }

            // Count total matching items before pagination
            int totalItems = await query.CountAsync();

            // Calculate total pages safely
            int totalPages = totalItems > 0
                ? (int)Math.Ceiling((double)totalItems / pageSize)
                : 1;

            // Normalize page number safely (prevent underflow and handle requests beyond last page)
            if (pageNumber < 1)
            {
                pageNumber = 1;
            }
            else if (totalItems > 0 && pageNumber > totalPages)
            {
                pageNumber = totalPages;
            }

            // Deterministic sorting with unique tie-breaker (SaleId)
            query = sortOrder switch
            {
                "date_asc" => query.OrderBy(s => s.SaleDate).ThenBy(s => s.SaleId),
                "amount_desc" => query.OrderByDescending(s => s.TotalAmount).ThenByDescending(s => s.SaleId),
                "amount_asc" => query.OrderBy(s => s.TotalAmount).ThenBy(s => s.SaleId),
                "customer_asc" => query.OrderBy(s => s.Customer != null ? s.Customer.Name : string.Empty).ThenBy(s => s.SaleId),
                "customer_desc" => query.OrderByDescending(s => s.Customer != null ? s.Customer.Name : string.Empty).ThenByDescending(s => s.SaleId),
                _ => query.OrderByDescending(s => s.SaleDate).ThenByDescending(s => s.SaleId)
            };

            // Calculate offset with zero integer overflow risk
            int skip = (int)(((long)pageNumber - 1) * pageSize);

            // Server-side paginated retrieval
            var pagedSales = await query
                .Skip(skip)
                .Take(pageSize)
                .ToListAsync();

            var viewModel = new SalesIndexViewModel
            {
                Sales = pagedSales,
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalItems = totalItems,
                SearchTerm = searchTerm,
                StartDate = startDate,
                EndDate = endDate,
                SortOrder = sortOrder
            };

            return View(viewModel);
        }


        // =========================================================
        // GET: Sales/Details/5
        // =========================================================

        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var sale = await _context.Sales
                .Include(s => s.Customer)
                .Include(s => s.SaleItems)
                    .ThenInclude(si => si.Product)
                .FirstOrDefaultAsync(m => m.SaleId == id);

            if (sale == null)
            {
                return NotFound();
            }

            return View(sale);
        }


        // =========================================================
        // GET: Sales/Create
        // =========================================================

        public async Task<IActionResult> Create()
        {
            // Load products with selling price

            ViewBag.Products = await _context.Products
                .Select(p => new
                {
                    p.ProductId,
                    p.Name,
                    p.SellingPrice
                })
                .ToListAsync();


            // Create empty sale model

            var model = new SaleCreateViewModel
            {
                SaleDate = DateTime.Now,
                CheckoutToken = Guid.NewGuid().ToString("N"),

                Items = new List<SaleItemViewModel>
                {
                    new SaleItemViewModel()
                }
            };

            return View(model);
        }


        // =========================================================
        // POST: Sales/Create
        // =========================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            SaleCreateViewModel model)
        {
            var result = await _salesCheckoutService.CreateSaleAsync(model, ModelState.IsValid);

            if (result.Success)
            {
                return RedirectToAction(nameof(Index));
            }

            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(error.FieldName, error.ErrorMessage);
            }

            model.CheckoutToken = Guid.NewGuid().ToString("N");
            await LoadCreateData();
            return View(model);
        }


        // =========================================================
        // GET: Sales/Edit/5
        // =========================================================

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var sale =
                await _context.Sales
                    .FindAsync(id);

            if (sale == null)
            {
                return NotFound();
            }


            ViewData["CustomerId"] =
                new SelectList(
                    await _context.Customers.ToListAsync(),
                    "CustomerId",
                    "Name",
                    sale.CustomerId);


            return View(sale);
        }


        // =========================================================
        // POST: Sales/Edit/5
        // =========================================================

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(
                "SaleId,CustomerId,SaleDate"
            )]
            Sale sale)
        {
            if (id != sale.SaleId)
            {
                return NotFound();
            }

            if (!await _context.Customers.AnyAsync(c => c.CustomerId == sale.CustomerId))
                ModelState.AddModelError("CustomerId", "Selected customer does not exist.");

            if (ModelState.IsValid)
            {
                try
                {
                    var existingSale = await _context.Sales.FindAsync(id);
                    if (existingSale == null)
                    {
                        return NotFound();
                    }

                    // Update permissible administrative metadata
                    existingSale.CustomerId = sale.CustomerId;
                    existingSale.SaleDate = sale.SaleDate;

                    // FINANCIAL & AUDIT INTEGRITY PRESERVATION:
                    // TotalAmount and Discount are derived from itemized SaleItems and category discount allocations.
                    // CheckoutToken is server-managed for durable idempotency.
                    // These fields are strictly preserved and cannot be altered via edit.

                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!SaleExists(sale.SaleId))
                    {
                        return NotFound();
                    }

                    throw;
                }


                return RedirectToAction(
                    nameof(Index));
            }


            ViewData["CustomerId"] =
                new SelectList(
                    await _context.Customers.ToListAsync(),
                    "CustomerId",
                    "Name",
                    sale.CustomerId);


            return View(sale);
        }


        // =========================================================
        // GET: Sales/Delete/5
        // =========================================================

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }


            var sale =
                await _context.Sales
                    .Include(s => s.Customer)
                    .Include(s => s.SaleItems)
                        .ThenInclude(si => si.Product)
                    .FirstOrDefaultAsync(
                        m => m.SaleId == id);


            if (sale == null)
            {
                return NotFound();
            }


            return View(sale);
        }


        // =========================================================
        // POST: Sales/Delete/5
        // =========================================================

        [HttpPost, ActionName("Delete")]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var result = await _salesCheckoutService.CancelSaleAsync(id);

            if (result.Success)
            {
                TempData["SuccessMessage"] = $"Sale #{id} was deleted and stock was restored.";
            }
            else if (result.NotFound)
            {
                TempData["WarningMessage"] = result.ErrorMessage ?? "Sale not found or has already been deleted.";
            }
            else if (result.AlreadyDeleted)
            {
                TempData["WarningMessage"] = result.ErrorMessage ?? "This sale has already been deleted by another user.";
            }
            else
            {
                TempData["ErrorMessage"] = result.ErrorMessage ?? "An error occurred while deleting the sale.";
            }

            return RedirectToAction(nameof(Index));
        }


        // =========================================================
        // Helper: Load Create Page Data
        // =========================================================

        private async Task LoadCreateData()
        {
            // Products

            ViewBag.Products =
                await _context.Products
                    .Select(p => new
                    {
                        p.ProductId,
                        p.Name,
                        p.SellingPrice
                    })
                    .ToListAsync();
        }


        // =========================================================
        // Check if Sale exists
        // =========================================================

        private bool SaleExists(int id)
        {
            return _context.Sales
                .Any(e => e.SaleId == id);
        }
    }
}

