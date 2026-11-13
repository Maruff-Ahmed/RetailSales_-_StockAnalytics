
using System.Text.Json;
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
    public class ComplaintRoutingController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ComplaintRoutingService _routingService;
        private readonly IComplaintCodeGenerator _codeGenerator;

        public ComplaintRoutingController(
            ApplicationDbContext context,
            ComplaintRoutingService routingService,
            IComplaintCodeGenerator codeGenerator)
        {
            _context = context;
            _routingService = routingService;
            _codeGenerator = codeGenerator;
        }


        // ==========================================
        // COMPLAINT LIST
        // ==========================================

        public async Task<IActionResult> Index()
        {
            var complaints =
                await _context.Complaints
                    .Include(c => c.Customer)
                    .Include(c => c.ComplaintCategory)
                    .OrderByDescending(c => c.CreatedAt)
                    .ToListAsync();

            return View(complaints);
        }


        // ==========================================
        // CREATE COMPLAINT - GET
        // ==========================================

        [HttpGet]
        public async Task<IActionResult> Create()
        {
            await LoadDropdowns();

            return View();
        }


        // ==========================================
        // CREATE COMPLAINT - POST
        // ==========================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            Complaint complaint,
            int ProductId)
        {
            // --------------------------------------
            // Validate complaint text
            // --------------------------------------

            if (string.IsNullOrWhiteSpace(
                complaint.ComplaintText))
            {
                ModelState.AddModelError(
                    "ComplaintText",
                    "Please enter a complaint.");

                await LoadDropdowns(
                    complaint.CustomerId,
                    ProductId);

                return View(complaint);
            }

            if (complaint.ComplaintText.Length > 4000)
            {
                ModelState.AddModelError(
                    "ComplaintText",
                    "Complaint text cannot exceed 4000 characters.");

                await LoadDropdowns(
                    complaint.CustomerId,
                    ProductId);

                return View(complaint);
            }

            // --------------------------------------
            // Validate customer exists
            // --------------------------------------

            var customerExists =
                await _context.Customers.AnyAsync(
                    c => c.CustomerId == complaint.CustomerId);

            if (!customerExists)
            {
                ModelState.AddModelError(
                    "CustomerId",
                    "Selected customer was not found.");

                await LoadDropdowns(
                    complaint.CustomerId,
                    ProductId);

                return View(complaint);
            }


            // --------------------------------------
            // Find selected product
            // --------------------------------------

            var product =
                await _context.Products
                    .Include(p => p.Category)
                    .FirstOrDefaultAsync(
                        p => p.ProductId == ProductId);


            if (product == null)
            {
                ModelState.AddModelError(
                    "",
                    "Selected product was not found.");

                await LoadDropdowns(
                    complaint.CustomerId,
                    ProductId);

                return View(complaint);
            }


            // --------------------------------------
            // Make sure product has a category
            // --------------------------------------

            if (product.Category == null)
            {
                ModelState.AddModelError(
                    "",
                    "The selected product does not have a category.");

                await LoadDropdowns(
                    complaint.CustomerId,
                    ProductId);

                return View(complaint);
            }


            // --------------------------------------
            // Run AI complaint routing
            // --------------------------------------

            var result =
                await _routingService.RouteComplaintAsync(
                    product.CategoryId,
                    complaint.ComplaintText.Trim());


            // --------------------------------------
            // Generate concurrency-safe complaint code
            // --------------------------------------

            string complaintCode = await _codeGenerator.GenerateNextCodeAsync();


            // --------------------------------------
            // Create clean new complaint (overposting defense)
            // --------------------------------------

            var newComplaint = new Complaint
            {
                ComplaintCode = complaintCode,
                CustomerId = complaint.CustomerId,
                ComplaintText = complaint.ComplaintText.Trim(),
                ComplaintCategoryId = result.ComplaintCategory?.ComplaintCategoryId,
                SimilarityScore = result.SimilarityScore,
                RoutingStatus = result.RoutingStatus,
                CreatedAt = DateTime.Now
            };


            // --------------------------------------
            // Save complaint with conflict handling
            // --------------------------------------

            try
            {
                _context.Complaints.Add(newComplaint);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                try
                {
                    _context.Entry(newComplaint).State = EntityState.Detached;
                    newComplaint.ComplaintCode = await _codeGenerator.GenerateNextCodeAsync();
                    _context.Complaints.Add(newComplaint);
                    await _context.SaveChangesAsync();
                }
                catch (Exception)
                {
                    ModelState.AddModelError("", "A duplicate complaint code conflict occurred. Please resubmit your complaint.");
                    await LoadDropdowns(complaint.CustomerId, ProductId);
                    return View(complaint);
                }
            }


            // --------------------------------------
            // Temporarily store ALL category scores
            // --------------------------------------

            TempData["CategoryScores"] =
                JsonSerializer.Serialize(
                    result.CategoryScores);


            // --------------------------------------
            // Show routing result
            // --------------------------------------

            return RedirectToAction(
                nameof(Result),
                new
                {
                    id = newComplaint.ComplaintId
                });
        }


        // ==========================================
        // ROUTING RESULT
        // ==========================================

        public async Task<IActionResult> Result(
            int id)
        {
            var complaint =
                await _context.Complaints
                    .Include(c => c.Customer)
                    .Include(c => c.ComplaintCategory)
                    .FirstOrDefaultAsync(
                        c => c.ComplaintId == id);


            if (complaint == null)
            {
                return NotFound();
            }


            // --------------------------------------
            // Retrieve category similarity scores
            // --------------------------------------

            if (TempData["CategoryScores"] != null)
            {
                var categoryScoresJson =
     TempData["CategoryScores"]?.ToString();

                if (!string.IsNullOrEmpty(categoryScoresJson))
                {
                    ViewBag.CategoryScores =
                        JsonSerializer.Deserialize<
                            List<CategorySimilarityResult>>(
                                categoryScoresJson);
                }
            }


            return View(complaint);
        }


        // ==========================================
        // DROPDOWNS
        // ==========================================

        private async Task LoadDropdowns(
            int? customerId = null,
            int? productId = null)
        {
            // --------------------------------------
            // Customers
            // --------------------------------------

            ViewBag.Customers =
                new SelectList(
                    await _context.Customers
                        .OrderBy(c => c.Name)
                        .ToListAsync(),

                    "CustomerId",
                    "Name",
                    customerId);


            // --------------------------------------
            // Products
            // --------------------------------------

            ViewBag.Products =
                new SelectList(
                    await _context.Products
                        .Include(p => p.Category)
                        .OrderBy(p => p.Name)
                        .ToListAsync(),

                    "ProductId",
                    "Name",
                    productId);
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                return sqlEx.Number == 2601 || sqlEx.Number == 2627;
            }
            return false;
        }
    }
}