using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using RetailSalesStockAnalytics.Data;
using RetailSalesStockAnalytics.Models;

namespace RetailSalesStockAnalytics.Controllers
{
    [Authorize(Roles = "Admin,Manager,Customer")]
    public class CustomersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CustomersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: Customers
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> Index()
        {
            return View(await _context.Customers.ToListAsync());
        }

        // GET: Customers/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers
                .FirstOrDefaultAsync(m => m.CustomerId == id);
            if (customer == null)
            {
                return NotFound();
            }

            // Resource ownership check: If user is a customer, they can only view their own record
            if (User.IsInRole("Customer") && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
            {
                if (string.IsNullOrEmpty(customer.Email) ||
                    !string.Equals(customer.Email, User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToAction("AccessDenied", "Account");
                }
            }

            return View(customer);
        }

        // GET: Customers/Create
        [Authorize(Roles = "Admin,Manager")]
        public IActionResult Create()
        {
            return View();
        }

        // POST: Customers/Create
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize(Roles = "Admin,Manager")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("CustomerId,Name,Phone,Email")] Customer customer)
        {
            if (ModelState.IsValid)
            {
                _context.Add(customer);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(customer);
        }

        // GET: Customers/Edit/5
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers.FindAsync(id);
            if (customer == null)
            {
                return NotFound();
            }
            return View(customer);
        }

        // POST: Customers/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [Authorize(Roles = "Admin,Manager")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("CustomerId,Name,Phone,Email")] Customer customer)
        {
            if (id != customer.CustomerId)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var existingCustomer = await _context.Customers.FindAsync(id);
                    if (existingCustomer == null)
                    {
                        return NotFound();
                    }

                    existingCustomer.Name = customer.Name;
                    existingCustomer.Phone = customer.Phone;
                    existingCustomer.Email = customer.Email;

                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!CustomerExists(customer.CustomerId))
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
            return View(customer);
        }

        // GET: Customers/Delete/5
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers
                .FirstOrDefaultAsync(m => m.CustomerId == id);
            if (customer == null)
            {
                return NotFound();
            }

            int salesCount = await _context.Sales.CountAsync(s => s.CustomerId == id);
            int complaintsCount = await _context.Complaints.CountAsync(c => c.CustomerId == id);
            ViewBag.HasHistoricalRecords = (salesCount > 0 || complaintsCount > 0);
            ViewBag.HistoricalSalesCount = salesCount;
            ViewBag.HistoricalComplaintsCount = complaintsCount;

            return View(customer);
        }

        // POST: Customers/Delete/5
        [HttpPost, ActionName("Delete")]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var customer = await _context.Customers.FindAsync(id);
            if (customer == null)
            {
                return NotFound();
            }

            int salesCount = await _context.Sales.CountAsync(s => s.CustomerId == id);
            int complaintsCount = await _context.Complaints.CountAsync(c => c.CustomerId == id);
            if (salesCount > 0 || complaintsCount > 0)
            {
                var reasons = new List<string>();
                if (salesCount > 0) reasons.Add($"{salesCount} historical sales transaction(s)");
                if (complaintsCount > 0) reasons.Add($"{complaintsCount} customer complaint record(s)");
                TempData["ErrorMessage"] = $"Cannot delete customer '{customer.Name}' because {string.Join(" and ", reasons)} exist. Historical records must be preserved.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                _context.Customers.Remove(customer);
                await _context.SaveChangesAsync();
                TempData["SuccessMessage"] = $"Customer '{customer.Name}' was deleted successfully.";
            }
            catch (DbUpdateException)
            {
                TempData["ErrorMessage"] = $"Cannot delete customer '{customer.Name}' because dependent historical records exist in the database.";
            }

            return RedirectToAction(nameof(Index));
        }

        private bool CustomerExists(int id)
        {
            return _context.Customers.Any(e => e.CustomerId == id);
        }
    }
}
