using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;
using RetailSalesStockAnalytics.Services;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager")]
    public class ProductsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IStockService _stockService;

        public ProductsController(
            ApplicationDbContext context,
            IStockService stockService)
        {
            _context = context;
            _stockService = stockService;
        }

        // GET: Products
        public async Task<IActionResult> Index()
        {
            var applicationDbContext = _context.Products.Include(p => p.Category);
            return View(await applicationDbContext.ToListAsync());
        }

        // GET: Products/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products
                .Include(p => p.Category)
                .FirstOrDefaultAsync(m => m.ProductId == id);
            if (product == null)
            {
                return NotFound();
            }

            return View(product);
        }

        // GET: Products/Create
        [Authorize(Roles = "Admin")]
        public IActionResult Create()
        {
            ViewData["CategoryId"] = new SelectList(_context.Categories, "CategoryId", "Name");
            return View();
        }

        // POST: Products/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ProductId,Name,CategoryId,PurchasePrice,SellingPrice,StockQuantity,ReorderLevel")] Product product)
        {
            if (product.PurchasePrice < 0)
                ModelState.AddModelError("PurchasePrice", "Purchase price cannot be negative.");
            if (product.SellingPrice < 0)
                ModelState.AddModelError("SellingPrice", "Selling price cannot be negative.");
            if (product.StockQuantity < 0)
                ModelState.AddModelError("StockQuantity", "Stock quantity cannot be negative.");
            if (product.ReorderLevel < 0)
                ModelState.AddModelError("ReorderLevel", "Reorder level cannot be negative.");

            if (!await _context.Categories.AnyAsync(c => c.CategoryId == product.CategoryId))
                ModelState.AddModelError("CategoryId", "Selected category does not exist.");

            if (ModelState.IsValid)
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    _context.Add(product);
                    await _context.SaveChangesAsync();

                    if (product.StockQuantity > 0)
                    {
                        await _stockService.RecordInitialStockAsync(
                            product.ProductId,
                            product.StockQuantity,
                            product.PurchasePrice);
                    }

                    await transaction.CommitAsync();
                    return RedirectToAction(nameof(Index));
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            ViewData["CategoryId"] = new SelectList(_context.Categories, "CategoryId", "Name", product.CategoryId);
            return View(product);
        }

        // GET: Products/Edit/5
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }
            ViewData["CategoryId"] = new SelectList(_context.Categories, "CategoryId", "Name", product.CategoryId);
            return View(product);
        }

        // POST: Products/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ProductId,Name,CategoryId,PurchasePrice,SellingPrice,StockQuantity,ReorderLevel")] Product product)
        {
            if (id != product.ProductId)
            {
                return NotFound();
            }

            if (product.PurchasePrice < 0)
                ModelState.AddModelError("PurchasePrice", "Purchase price cannot be negative.");
            if (product.SellingPrice < 0)
                ModelState.AddModelError("SellingPrice", "Selling price cannot be negative.");
            if (product.StockQuantity < 0)
                ModelState.AddModelError("StockQuantity", "Stock quantity cannot be negative.");
            if (product.ReorderLevel < 0)
                ModelState.AddModelError("ReorderLevel", "Reorder level cannot be negative.");

            if (!await _context.Categories.AnyAsync(c => c.CategoryId == product.CategoryId))
                ModelState.AddModelError("CategoryId", "Selected category does not exist.");

            if (ModelState.IsValid)
            {
                var existing = await _context.Products.FindAsync(id);
                if (existing == null)
                {
                    return NotFound();
                }

                int oldStock = existing.StockQuantity;

                existing.Name = product.Name;
                existing.CategoryId = product.CategoryId;
                existing.PurchasePrice = product.PurchasePrice;
                existing.SellingPrice = product.SellingPrice;
                existing.StockQuantity = product.StockQuantity;
                existing.ReorderLevel = product.ReorderLevel;

                try
                {
                    if (product.StockQuantity != oldStock)
                    {
                        await _stockService.RecordProductEditAdjustmentAsync(
                            existing.ProductId,
                            oldStock,
                            product.StockQuantity,
                            product.PurchasePrice);
                    }

                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ProductExists(product.ProductId))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            ViewData["CategoryId"] = new SelectList(_context.Categories, "CategoryId", "Name", product.CategoryId);
            return View(product);
        }

        // GET: Products/Delete/5
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products
                .Include(p => p.Category)
                .FirstOrDefaultAsync(m => m.ProductId == id);
            if (product == null)
            {
                return NotFound();
            }

            int saleItemsCount = await _context.SaleItems.CountAsync(si => si.ProductId == id);
            int stockTransactionsCount = await _context.StockTransactions.CountAsync(st => st.ProductId == id);

            ViewBag.HasHistoricalRecords = (saleItemsCount > 0 || stockTransactionsCount > 0);
            ViewBag.SaleItemsCount = saleItemsCount;
            ViewBag.StockTransactionsCount = stockTransactionsCount;

            return View(product);
        }

        // POST: Products/Delete/5
        [HttpPost, ActionName("Delete")]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }

            int saleItemsCount = await _context.SaleItems.CountAsync(si => si.ProductId == id);
            int stockTransactionsCount = await _context.StockTransactions.CountAsync(st => st.ProductId == id);

            if (saleItemsCount > 0 || stockTransactionsCount > 0)
            {
                var reasons = new List<string>();
                if (saleItemsCount > 0) reasons.Add($"{saleItemsCount} historical sales item(s)");
                if (stockTransactionsCount > 0) reasons.Add($"{stockTransactionsCount} stock audit transaction(s)");

                TempData["ErrorMessage"] = $"Cannot delete product '{product.Name}' because {string.Join(" and ", reasons)} exist. Historical records must be preserved.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                _context.Products.Remove(product);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Product '{product.Name}' was deleted successfully.";
            }
            catch (DbUpdateException)
            {
                TempData["ErrorMessage"] = $"Cannot delete product '{product.Name}' because dependent historical records exist in the database.";
            }

            return RedirectToAction(nameof(Index));
        }

        private bool ProductExists(int id)
        {
            return _context.Products.Any(e => e.ProductId == id);
        }
    }
}
